using System.Net;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Core.Proxy;
using AeroGatePilot.Infrastructure.Api;
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
/// Turns this PC into a captive-portal gateway: Ethernet (WAN) is shared over Windows Mobile Hotspot
/// or a LAN NIC (switch / MikroTik / APs). ICS provides NAT/DHCP/DNS; portal + packet filter enforce access.
/// </summary>
public sealed class GatewayEngine : IPortalBackend, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private readonly HotspotManager _hotspot = new();
    private readonly IcsShareManager _icsShare = new();
    private readonly NetworkSnapshot _snapshot;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HostAddressCache _names = new();
    private readonly ApDirectory _aps = new();
    private readonly RedirectTable _redirects = new();
    private readonly WebhookClient _webhook;
    private readonly AdminApiServer _api;
    private PortalServer? _portal;
    private PacketFilter? _filter;
    private TransparentRelay? _relay;
    private XrayCoreHost? _xray;
    private Timer? _timer;
    private GatewayMode _activeMode = GatewayMode.MobileHotspot;

    public GatewayEngine(AppPaths paths, SettingsStore settings, SqliteStore store, AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
        Store = store;
        _snapshot = new NetworkSnapshot(paths.SnapshotFile);
        Xray = new XrayCoreHost(XrayCoreHost.DefaultCoreDirectory, paths.CoreDataDirectory, log);
        _webhook = new WebhookClient(log, store);
        Sessions = new SessionManager(store);
        Sessions.ZoneForMac = mac => _aps.Match(mac, _settings.Current.ApZones);
        Sessions.SessionStarted += s => _log.Success($"{s.User.Username} signed in from {IpUtil.Format(s.Ip)} {s.Mac} {s.ApName}".TrimEnd());
        Sessions.SessionEnded += (s, reason) =>
        {
            _log.Info($"{s.User.Username} ({IpUtil.Format(s.Ip)}) disconnected: {reason} — {Formatting.Bytes(s.DownloadBytes + s.UploadBytes)} used");
            _webhook.NotifySessionEnd(s.User.Username, s.DownloadBytes, s.UploadBytes, s.User.UsedTotalBytes, IpUtil.Format(s.Ip), s.Mac, reason.ToString());
        };
        Sessions.UsageFlushed += (s, _, _, _) =>
            _webhook.NotifyFlush(s.User.Username, s.User.UsedDownloadBytes, s.User.UsedUploadBytes, s.User.UsedTotalBytes, IpUtil.Format(s.Ip), s.Mac);
        _api = new AdminApiServer(this, store, log);
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
    public AdminApiServer Api => _api;
    public bool VpnRelayActive => _relay is not null;
    public long VpnActiveConnections => _relay?.ActiveConnections ?? 0;
    public long VpnFailedConnections => _relay?.FailedConnections ?? 0;
    public int HotspotClientCount => _activeMode == GatewayMode.MobileHotspot ? _hotspot.ClientCount : Sessions.Count;
    public IReadOnlyList<HotspotClient> HotspotClients => _hotspot.GetClients();
    public GatewayMode ActiveMode => _activeMode;

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
            _webhook.Configure(settings.Webhook);
            _activeMode = settings.Wifi.Mode;

            var profile = HotspotManager.FindProfile(settings.Wifi.WanAdapterId)
                          ?? throw new InvalidOperationException("No internet connection found to share. Connect the Ethernet cable and try again.");
            WanName = profile.ProfileName;

            IPAddress gateway;
            (System.Net.NetworkInformation.NetworkInterface Interface, int PrefixLength) nic;

            if (_activeMode == GatewayMode.LanShare)
            {
                var wanId = IcsShareManager.ResolveWanAdapterId(settings.Wifi.WanAdapterId)
                            ?? throw new InvalidOperationException("Could not resolve the WAN adapter id.");
                if (!_snapshot.Exists)
                    _snapshot.Save(new NetworkSnapshotData(DateTime.UtcNow, null));

                _log.Info($"Starting LAN share: \"{WanName}\" → LAN adapter (switch / MikroTik / APs)…");
                await Task.Run(() => _icsShare.Start(wanId, settings.Wifi.LanAdapterId, _log));

                gateway = NetworkDiagnostics.IcsGatewayAddress();
                nic = await WaitForInterfaceAsync(gateway, TimeSpan.FromSeconds(60)
                      ) ?? throw new InvalidOperationException(
                        $"LAN sharing started but the shared interface ({gateway}) did not come up. Check the LAN cable to the switch / MikroTik.");
                _log.Info($"LAN interface \"{nic.Interface.Description}\" is up at {gateway}/{nic.PrefixLength}.");

                if (!await IcsRepair.WaitUntilServingAsync(gateway, TimeSpan.FromSeconds(25)))
                {
                    _log.Warn("ICS DHCP did not start — clearing and retrying LAN share…");
                    _icsShare.Stop(_log);
                    await Task.Run(IcsRepair.RestartSharingService);
                    await Task.Run(() => _icsShare.Start(wanId, settings.Wifi.LanAdapterId, _log));
                    nic = await WaitForInterfaceAsync(gateway, TimeSpan.FromSeconds(45))
                          ?? throw new InvalidOperationException($"After repairing ICS the LAN interface ({gateway}) did not come up.");
                    if (!await IcsRepair.WaitUntilServingAsync(gateway, TimeSpan.FromSeconds(30)))
                        throw new InvalidOperationException(
                            "Windows Internet Connection Sharing did not start DHCP on the LAN adapter. Restart Windows and try again.");
                    _log.Success("LAN Internet Connection Sharing repaired.");
                }
            }
            else
            {
                if (!_snapshot.Exists)
                    _snapshot.Save(new NetworkSnapshotData(DateTime.UtcNow, _hotspot.Capture(profile)));

                if (settings.Wifi.OpenNetwork)
                    _log.Warn("Open (password-less) Wi-Fi is not supported by Windows Mobile Hotspot; WPA2 is used instead.");

                _log.Info($"Starting Mobile Hotspot \"{settings.Wifi.Ssid}\" sharing \"{WanName}\"…");
                await _hotspot.StartAsync(profile, settings.Wifi);

                gateway = NetworkDiagnostics.IcsGatewayAddress();
                nic = await WaitForInterfaceAsync(gateway, TimeSpan.FromSeconds(60))
                      ?? throw new InvalidOperationException($"The hotspot started but its interface ({gateway}) did not come up within 60 seconds. Check that Mobile Hotspot can be turned on in Windows Settings.");
                _log.Info($"Hotspot interface \"{nic.Interface.Description}\" is up at {gateway}/{nic.PrefixLength}.");

                if (!await IcsRepair.WaitUntilServingAsync(gateway, TimeSpan.FromSeconds(20)))
                    nic = await RepairSharingAsync(profile, settings.Wifi, gateway);
            }

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
                DirectDomains = upstream.DirectDomains ?? "",
            };
            _filter = new PacketFilter(Sessions, options, _log, _names, _redirects, _aps);
            _filter.Start();

            try
            {
                await _api.StartAsync(settings.Api);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Admin API failed to start: {ex.Message}", ex);
            }

            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            StartedUtc = DateTime.UtcNow;
            SetState(GatewayState.Running);
            if (_activeMode == GatewayMode.LanShare)
                _log.Success($"AeroGate is live (LAN share) — clients behind MikroTik / APs are sent to http://{gateway}/portal/");
            else
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
            await _api.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stopping Admin API", ex);
        }

        if (_activeMode == GatewayMode.LanShare)
        {
            try
            {
                _icsShare.Stop(_log);
            }
            catch (Exception ex)
            {
                _log.Error("Stopping LAN share", ex);
            }
        }
        else
        {
            try
            {
                await _hotspot.StopAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Stopping hotspot", ex);
            }
        }

        if (_snapshot.Exists)
            await _snapshot.RestoreAsync(_log);

        GatewayAddress = null;
        Subnet = null;
        StartedUtc = null;
    }

    public int KickUser(string username)
    {
        var user = Store.FindUserByName(username);
        if (user is null || user.IsGuest)
            return 0;
        var count = Sessions.Sessions.Count(s => s.User.Id == user.Id);
        Sessions.EndUserSessions(user.Id, AccessDenyReason.KickedByAdmin);
        return count;
    }

    public IReadOnlyList<ObservedClient> ListObservedClients()
    {
        var sessions = Sessions.Sessions.ToList();
        var byIp = new Dictionary<string, ObservedClient>(StringComparer.OrdinalIgnoreCase);
        if (Subnet is { } subnet)
        {
            foreach (var (ip, mac) in LanNeighbors.List(subnet))
            {
                var session = sessions.FirstOrDefault(s => IpUtil.Format(s.Ip) == ip);
                var ap = session?.ApName ?? _aps.Match(mac, Settings.ApZones)?.Name ?? "";
                byIp[ip] = new ObservedClient(ip, mac, session?.User.Username, session is not null, ap);
            }
        }
        foreach (var session in sessions)
        {
            var ip = IpUtil.Format(session.Ip);
            if (!byIp.ContainsKey(ip))
                byIp[ip] = new ObservedClient(ip, session.Mac, session.User.Username, true, session.ApName);
        }
        return byIp.Values.OrderBy(c => c.Ip).ToList();
    }

    private void Tick()
    {
        try
        {
            Sessions.Tick();
            _ = _webhook.DrainAsync();
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

        if (settings.Wifi.Mode == GatewayMode.LanShare)
        {
            if (string.IsNullOrWhiteSpace(settings.Wifi.LanAdapterId))
                throw new InvalidOperationException("Select the LAN adapter that connects to the switch / MikroTik (Network page → Gateway mode → LAN share).");
            var wanId = IcsShareManager.ResolveWanAdapterId(settings.Wifi.WanAdapterId) ?? "";
            if (wanId.Length > 0 && IcsShareManager.NormalizeGuid(settings.Wifi.LanAdapterId).Equals(wanId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WAN and LAN adapters must be different network cards.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Wifi.Ssid) || settings.Wifi.Ssid.Length > 32)
                throw new InvalidOperationException("The Wi-Fi name (SSID) must be 1–32 characters.");
            if (settings.Wifi.OpenNetwork)
                throw new InvalidOperationException(
                    "Windows Mobile Hotspot cannot broadcast an open (password-less) network. Use LAN share and set each modem SSID to Open. Phones connect with no Wi-Fi password and still sign in on the portal.");
            if (settings.Wifi.Passphrase.Length is < 8 or > 63)
                throw new InvalidOperationException("The Wi-Fi password must be 8–63 characters.");
        }

        if (settings.Portal.InternalPort is < 1024 or > 65535 || settings.Portal.InternalPort == 80)
            throw new InvalidOperationException("The portal port must be between 1024 and 65535.");
        if (!NetworkDiagnostics.IsPortFree(settings.Portal.InternalPort))
            throw new InvalidOperationException($"Portal port {settings.Portal.InternalPort} is already in use by another program.");

        if (settings.Api.Enabled)
        {
            if (string.IsNullOrWhiteSpace(settings.Api.Token) || settings.Api.Token.Length < 16)
                throw new InvalidOperationException("Admin API is enabled but the token is missing (min 16 characters).");
            if (settings.Api.Port is < 1024 or > 65535 || settings.Api.Port == settings.Portal.InternalPort)
                throw new InvalidOperationException("Admin API port must be 1024–65535 and different from the portal port.");
            if (!NetworkDiagnostics.IsPortFree(settings.Api.Port))
                throw new InvalidOperationException($"Admin API port {settings.Api.Port} is already in use.");
        }

        if (settings.Webhook.Enabled && string.IsNullOrWhiteSpace(settings.Webhook.Url))
            throw new InvalidOperationException("Webhook is enabled but the URL is empty.");

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
        await _webhook.DisposeAsync();
        await _api.DisposeAsync();
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
