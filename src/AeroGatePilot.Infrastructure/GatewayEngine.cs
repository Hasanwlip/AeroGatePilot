using System.Net;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Core.Proxy;
using AeroGatePilot.Infrastructure.Data;
using AeroGatePilot.Infrastructure.Network;
using AeroGatePilot.Infrastructure.Proxy;
using AeroGatePilot.Portal;

namespace AeroGatePilot.Infrastructure;

public enum GatewayState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

/// <summary>
/// Turns this PC into a captive-portal gateway: Ethernet (WAN) is shared over Windows Mobile Hotspot (Wi-Fi),
/// ICS provides NAT/DHCP/DNS, the portal authenticates users and the packet filter enforces access and quotas.
/// </summary>
public sealed class GatewayEngine : IPortalBackend, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private readonly HotspotManager _hotspot = new();
    private readonly NetworkSnapshot _snapshot;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HostAddressCache _names = new();
    private readonly RedirectTable _redirects = new();
    private PortalServer? _portal;
    private PacketFilter? _filter;
    private TransparentRelay? _relay;
    private XrayCoreHost? _xray;
    private Timer? _timer;

    public GatewayEngine(AppPaths paths, SettingsStore settings, SqliteStore store, AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
        Store = store;
        _snapshot = new NetworkSnapshot(paths.SnapshotFile);
        Xray = new XrayCoreHost(XrayCoreHost.DefaultCoreDirectory, paths.CoreDataDirectory, log);
        Sessions = new SessionManager(store);
        Sessions.SessionStarted += s => _log.Success($"{s.User.Username} signed in from {IpUtil.Format(s.Ip)} {s.Mac}".TrimEnd());
        Sessions.SessionEnded += (s, reason) => _log.Info($"{s.User.Username} ({IpUtil.Format(s.Ip)}) disconnected: {reason} — {Formatting.Bytes(s.DownloadBytes + s.UploadBytes)} used");
    }

    public SqliteStore Store { get; }
    public SessionManager Sessions { get; }
    public GatewayState State { get; private set; } = GatewayState.Stopped;
    public string LastError { get; private set; } = "";
    public IPAddress? GatewayAddress { get; private set; }
    public Ipv4Subnet? Subnet { get; private set; }
    public string WanName { get; private set; } = "";
    public DateTime? StartedUtc { get; private set; }
    public PacketFilterStats? FilterStats => _filter?.Stats;
    public XrayCoreHost Xray { get; }
    public bool VpnRelayActive => _relay is not null;
    public long VpnActiveConnections => _relay?.ActiveConnections ?? 0;
    public long VpnFailedConnections => _relay?.FailedConnections ?? 0;
    public int HotspotClientCount => _hotspot.ClientCount;
    public IReadOnlyList<HotspotClient> HotspotClients => _hotspot.GetClients();

    public event Action? StateChanged;

    // ---------- IPortalBackend ----------

    public AppSettings Settings => _settings.Current;
    public string BrandingDirectory => _paths.BrandingDirectory;
    public string TemplateDirectory => _paths.TemplateDirectory;

    public PortalClientStatus? GetStatus(IPAddress client) => Sessions.GetStatus(client);

    public LoginResult Login(string username, string password, IPAddress client)
    {
        if (State != GatewayState.Running)
            return LoginResult.Fail(AccessDenyReason.GatewayNotRunning);
        var result = Sessions.Login(username, password, client, MacResolver.Resolve(client));
        if (!result.Success)
            _log.Warn($"Sign-in rejected for \"{username}\" from {IpUtil.Format(client)}: {result.Reason}");
        return result;
    }

    public LoginResult GuestLogin(IPAddress client)
    {
        if (State != GatewayState.Running)
            return LoginResult.Fail(AccessDenyReason.GatewayNotRunning);
        var result = Sessions.GuestLogin(client, MacResolver.Resolve(client), Settings.Portal.GuestPlanId);
        if (!result.Success)
            _log.Warn($"Guest access rejected for {IpUtil.Format(client)}: {result.Reason}");
        return result;
    }

    public void Logout(IPAddress client) => Sessions.Logout(client);

    // ---------- Lifecycle ----------

    /// <summary>Restores hotspot settings left behind by a crash or forced shutdown.</summary>
    public async Task RecoverAsync()
    {
        if (_snapshot.Exists)
        {
            _log.Warn("AeroGate Pilot did not shut down cleanly last time — restoring network settings.");
            await _snapshot.RestoreAsync(_log);
        }
    }

    public async Task<bool> StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State is GatewayState.Running)
                return true;
            SetState(GatewayState.Starting);
            LastError = "";
            var settings = _settings.Current;

            Validate(settings);

            var profile = HotspotManager.FindProfile(settings.Wifi.WanAdapterId)
                          ?? throw new InvalidOperationException("No internet connection found to share. Connect the Ethernet cable and try again.");
            WanName = profile.ProfileName;

            if (!_snapshot.Exists)
                _snapshot.Save(new NetworkSnapshotData(DateTime.UtcNow, _hotspot.Capture(profile)));

            if (settings.Wifi.OpenNetwork)
                _log.Warn("Open (password-less) Wi-Fi is not supported by Windows Mobile Hotspot; WPA2 is used instead.");

            _log.Info($"Starting Mobile Hotspot \"{settings.Wifi.Ssid}\" sharing \"{WanName}\"…");
            await _hotspot.StartAsync(profile, settings.Wifi);

            var gateway = NetworkDiagnostics.IcsGatewayAddress();
            var nic = await WaitForInterfaceAsync(gateway, TimeSpan.FromSeconds(60))
                      ?? throw new InvalidOperationException($"The hotspot started but its interface ({gateway}) did not come up within 60 seconds. Check that Mobile Hotspot can be turned on in Windows Settings.");
            _log.Info($"Hotspot interface \"{nic.Interface.Description}\" is up at {gateway}/{nic.PrefixLength}.");

            if (!await IcsRepair.WaitUntilServingAsync(gateway, TimeSpan.FromSeconds(20)))
                nic = await RepairSharingAsync(profile, settings.Wifi, gateway);

            GatewayAddress = gateway;
            Subnet = Ipv4Subnet.From(gateway, nic.PrefixLength);

            var upstream = settings.Upstream;
            if (upstream.Enabled)
                FirewallRule.Allow(settings.Portal.InternalPort, upstream.RelayPort);
            else
                FirewallRule.Allow(settings.Portal.InternalPort);

            // Binding to the hotspot address itself fails while Windows still treats it as tentative,
            // so listen on all addresses and let the portal ignore anything not addressed to the gateway.
            _portal = new PortalServer(this);
            await _portal.StartAsync(IPAddress.Any, settings.Portal.InternalPort, gateway.ToString(), gateway);

            UpstreamProxySettings? relayUpstream = null;
            if (upstream.Mode == UpstreamMode.BuiltInCore)
            {
                var server = upstream.ActiveServer
                    ?? throw new InvalidOperationException("Import a VLESS / VMess / Trojan / SS link and select it before starting.");
                _xray = Xray;
                await _xray.StartAsync(server, upstream.CoreSocksPort);
                relayUpstream = new UpstreamProxySettings
                {
                    Mode = UpstreamMode.ExternalPort,
                    Type = UpstreamProxyType.Socks5,
                    Host = "127.0.0.1",
                    Port = _xray.SocksPort,
                    FallbackDirect = upstream.FallbackDirect,
                    RelayPort = upstream.RelayPort,
                    Scope = upstream.Scope,
                };
            }
            else if (upstream.Mode == UpstreamMode.ExternalPort)
            {
                relayUpstream = upstream;
            }

            if (relayUpstream is not null)
            {
                _relay = new TransparentRelay(_redirects, _names, UpstreamConnector.Create(relayUpstream), relayUpstream.FallbackDirect, gateway, _log);
                _relay.Start(relayUpstream.RelayPort);
                _ = CheckUpstreamAsync(relayUpstream);
            }

            var options = new PacketFilterOptions(IpUtil.ToKey(gateway), Subnet.Value, settings.Portal.InternalPort, settings.Portal.IsolateHost)
            {
                HotspotIfIndex = (uint)nic.Interface.GetIPProperties().GetIPv4Properties().Index,
                RelayPort = relayUpstream?.RelayPort ?? 0,
                ProxyAllUsers = upstream.Scope == UpstreamProxyScope.AllUsers,
            };
            _filter = new PacketFilter(Sessions, options, _log, _names, _redirects);
            _filter.Start();

            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            StartedUtc = DateTime.UtcNow;
            SetState(GatewayState.Running);
            _log.Success($"AeroGate is live — clients joining \"{settings.Wifi.Ssid}\" are sent to http://{gateway}/portal/");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("Start failed", ex);
            await TearDownAsync();
            SetState(GatewayState.Faulted);
            return false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State is GatewayState.Stopped)
                return;
            SetState(GatewayState.Stopping);
            await TearDownAsync();
            SetState(GatewayState.Stopped);
            _log.Info("Gateway stopped and network settings restored.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Kick(ClientSession session) => Sessions.EndUserSessions(session.User.Id, AccessDenyReason.KickedByAdmin);

    public void KickDevice(ClientSession session) => Sessions.Logout(session.Ip, AccessDenyReason.KickedByAdmin);

    private async Task TearDownAsync()
    {
        _timer?.Dispose();
        _timer = null;
        Sessions.EndAll(AccessDenyReason.GatewayStopped);

        try
        {
            _filter?.Stop();
        }
        catch (Exception ex)
        {
            _log.Error("Stopping packet filter", ex);
        }
        _filter = null;

        if (_relay is not null)
        {
            try
            {
                await _relay.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Stopping VPN relay", ex);
            }
            _relay = null;
        }

        if (_xray is not null)
        {
            try
            {
                await _xray.StopAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Stopping Xray core", ex);
            }
            _xray = null;
        }

        if (_portal is not null)
        {
            try
            {
                await _portal.StopAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Stopping portal", ex);
            }
            _portal = null;
        }

        try
        {
            FirewallRule.Remove();
        }
        catch (Exception ex)
        {
            _log.Error("Removing firewall rule", ex);
        }

        try
        {
            await _hotspot.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stopping hotspot", ex);
        }

        if (_snapshot.Exists)
            await _snapshot.RestoreAsync(_log);

        GatewayAddress = null;
        Subnet = null;
        StartedUtc = null;
    }

    private void Tick()
    {
        try
        {
            Sessions.Tick();
        }
        catch (Exception ex)
        {
            _log.Error("Session accounting", ex);
        }
    }

    private void Validate(AppSettings settings)
    {
        if (!NetworkDiagnostics.IsAdministrator())
            throw new InvalidOperationException("Administrator rights are required. Restart AeroGate Pilot with \"Run as administrator\".");
        if (!WinDivertNative.IsAvailable(out var error))
            throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(settings.Wifi.Ssid) || settings.Wifi.Ssid.Length > 32)
            throw new InvalidOperationException("The Wi-Fi name (SSID) must be 1–32 characters.");
        if (settings.Wifi.Passphrase.Length is < 8 or > 63)
            throw new InvalidOperationException("The Wi-Fi password must be 8–63 characters.");
        if (settings.Portal.InternalPort is < 1024 or > 65535 || settings.Portal.InternalPort == 80)
            throw new InvalidOperationException("The portal port must be between 1024 and 65535.");
        if (!NetworkDiagnostics.IsPortFree(settings.Portal.InternalPort))
            throw new InvalidOperationException($"Portal port {settings.Portal.InternalPort} is already in use by another program.");

        var upstream = settings.Upstream;
        if (upstream.Mode == UpstreamMode.BuiltInCore)
        {
            if (!Xray.IsInstalled)
                throw new InvalidOperationException("Xray core (core\\xray.exe) is missing. Reinstall AeroGate Pilot or copy Xray-windows-64 into the core folder.");
            if (upstream.ActiveServer is null)
                throw new InvalidOperationException("Import a VLESS / VMess / Trojan / SS link on the Network page and select it.");
            if (upstream.RelayPort is < 1024 or > 65535 || upstream.RelayPort == settings.Portal.InternalPort)
                throw new InvalidOperationException("The VPN relay port must be between 1024 and 65535 and differ from the portal port.");
            if (!NetworkDiagnostics.IsPortFree(upstream.RelayPort))
                throw new InvalidOperationException($"VPN relay port {upstream.RelayPort} is already in use by another program.");
        }
        else if (upstream.Mode == UpstreamMode.ExternalPort)
        {
            if (string.IsNullOrWhiteSpace(upstream.Host) || upstream.Port is < 1 or > 65535)
                throw new InvalidOperationException("Enter the VPN proxy address and port (for example 127.0.0.1 and 10808), or turn VPN routing off.");
            if (upstream.RelayPort is < 1024 or > 65535 || upstream.RelayPort == settings.Portal.InternalPort || upstream.RelayPort == upstream.Port)
                throw new InvalidOperationException("The VPN relay port must be between 1024 and 65535 and differ from the portal and VPN ports.");
            if (!NetworkDiagnostics.IsPortFree(upstream.RelayPort))
                throw new InvalidOperationException($"VPN relay port {upstream.RelayPort} is already in use by another program.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Xray.DisposeAsync();
        _lifecycle.Dispose();
    }

    /// <summary>Checks that the VPN port works through a short test request.</summary>
    public static async Task<TimeSpan> TestUpstreamAsync(UpstreamProxySettings upstream, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            return await UpstreamConnector.TestAsync(UpstreamConnector.Create(upstream), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The VPN port did not answer within 12 seconds.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new IOException($"Nothing answers at {upstream.Host}:{upstream.Port} — is the VPN app running with its local proxy enabled? ({ex.Message})");
        }
    }

    private async Task CheckUpstreamAsync(UpstreamProxySettings upstream)
    {
        try
        {
            var elapsed = await TestUpstreamAsync(upstream);
            _log.Success($"VPN port {upstream.Host}:{upstream.Port} works ({elapsed.TotalMilliseconds:0} ms).");
        }
        catch (Exception ex)
        {
            _log.Warn($"VPN port check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The hotspot is on but Windows Internet Connection Sharing never started its DHCP server, so phones would join
    /// without an IP address. Clears stale sharing entries, restarts the service and starts the hotspot again.
    /// </summary>
    private async Task<(System.Net.NetworkInformation.NetworkInterface Interface, int PrefixLength)> RepairSharingAsync(
        Windows.Networking.Connectivity.ConnectionProfile profile, WifiSettings wifi, IPAddress gateway)
    {
        _log.Warn("Windows Internet Connection Sharing did not start (phones would get no IP address) — repairing it…");
        await _hotspot.StopAsync();

        foreach (var id in IcsRepair.ClearSharingEntries())
            _log.Info($"Cleared stale sharing entry for adapter {{{id}}}.");
        await Task.Run(IcsRepair.RestartSharingService);

        await _hotspot.StartAsync(profile, wifi);
        var nic = await WaitForInterfaceAsync(gateway, TimeSpan.FromSeconds(60))
                  ?? throw new InvalidOperationException($"After repairing Internet Connection Sharing the hotspot interface ({gateway}) did not come up.");

        if (!await IcsRepair.WaitUntilServingAsync(gateway, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException(
                "Windows Internet Connection Sharing does not start, so phones cannot get an IP address. Restart Windows and try again; " +
                "if it persists, remove VPN software that blocks sharing (for example Cisco AnyConnect) or use Settings › Network & internet › Network reset.");

        _log.Success("Internet Connection Sharing repaired.");
        return nic;
    }

    private static async Task<(System.Net.NetworkInformation.NetworkInterface Interface, int PrefixLength)?> WaitForInterfaceAsync(IPAddress address, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var nic = NetworkDiagnostics.FindInterfaceWithAddress(address, requireUsable: true);
            if (nic is not null)
                return nic;
            await Task.Delay(500);
        }
        return null;
    }

    private void SetState(GatewayState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
