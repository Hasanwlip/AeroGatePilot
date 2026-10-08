using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Proxy;
using AeroGatePilot.Core.Security;
using AeroGatePilot.Infrastructure;
using AeroGatePilot.Infrastructure.Network;

namespace AeroGatePilot.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record VpnPreset(UpstreamProxyType Type, int Port);

public sealed class VpnServerRow : ObservableObject
{
    private int _pingMs = -1;
    private bool _selected;

    public VpnServerRow(VpnServerProfile profile)
    {
        Profile = profile;
        _pingMs = profile.LastPingMs;
    }

    public VpnServerProfile Profile { get; }
    public string Name => Profile.Name;
    public string Protocol => Profile.ProtocolLabel;
    public string Endpoint => Profile.Endpoint;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public int PingMs
    {
        get => _pingMs;
        set
        {
            if (Set(ref _pingMs, value))
            {
                Profile.LastPingMs = value;
                Raise(nameof(PingText));
            }
        }
    }

    public string PingText => _pingMs switch
    {
        < -1 => Loc.T("Vpn_PingFail"),
        -1 => "—",
        _ => $"{_pingMs} ms",
    };

    public void RefreshLabels() => Raise(nameof(PingText));
}

public sealed class NetworkViewModel : PageViewModel
{
    private WifiSettings _wifi = new();
    private int _portalPort;
    private bool _isolateHost;
    private string _message = "";
    private bool _messageIsError;
    private UpstreamProxySettings _upstream = new();
    private string _vpnTestResult = "";
    private bool _vpnTestOk;
    private string _importText = "";
    private VpnServerRow? _selectedServer;

    public NetworkViewModel(AppServices services)
        : base(services)
    {
        SaveCommand = new RelayCommand(Save);
        TestVpnCommand = new AsyncRelayCommand(TestVpnAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAdaptersAsync);
        ImportCommand = new RelayCommand(ImportLinks);
        PasteImportCommand = new RelayCommand(PasteAndImport);
        RemoveServerCommand = new RelayCommand(RemoveSelected, () => _selectedServer is not null);
        PingAllCommand = new AsyncRelayCommand(PingAllAsync);
        PingSelectedCommand = new AsyncRelayCommand(PingSelectedAsync, () => _selectedServer is not null);
        GeneratePasswordCommand = new RelayCommand(() =>
        {
            _wifi.Passphrase = PasswordHasher.GeneratePassword(12);
            Raise(nameof(Wifi));
        });
        LoadFromSettings();
    }

    public override string Key => "network";

    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand GeneratePasswordCommand { get; }
    public AsyncRelayCommand TestVpnCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand PasteImportCommand { get; }
    public RelayCommand RemoveServerCommand { get; }
    public AsyncRelayCommand PingAllCommand { get; }
    public AsyncRelayCommand PingSelectedCommand { get; }

    public ObservableCollection<VpnServerRow> Servers { get; } = [];

    public UpstreamProxySettings Upstream
    {
        get => _upstream;
        private set => Set(ref _upstream, value);
    }

    public UpstreamMode VpnMode
    {
        get => _upstream.Mode;
        set
        {
            _upstream.Mode = value;
            Raise();
            Raise(nameof(IsCoreMode));
            Raise(nameof(IsExternalMode));
            Raise(nameof(VpnEnabled));
        }
    }

    public bool IsCoreMode => _upstream.Mode == UpstreamMode.BuiltInCore;
    public bool IsExternalMode => _upstream.Mode == UpstreamMode.ExternalPort;
    public bool VpnEnabled => _upstream.Mode != UpstreamMode.Off;
    public bool CoreInstalled => Services.Engine.Xray.IsInstalled;
    public string CoreStatus => CoreInstalled ? Loc.T("Vpn_CoreReady") : Loc.T("Vpn_CoreMissing");

    public string ImportText
    {
        get => _importText;
        set => Set(ref _importText, value);
    }

    public VpnServerRow? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!Set(ref _selectedServer, value))
                return;
            foreach (var row in Servers)
                row.Selected = row == value;
            if (value is not null)
                _upstream.ActiveServerId = value.Profile.Id;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public UpstreamProxyType VpnType
    {
        get => _upstream.Type;
        set
        {
            _upstream.Type = value;
            Raise();
        }
    }

    public UpstreamProxyScope VpnScope
    {
        get => _upstream.Scope;
        set
        {
            _upstream.Scope = value;
            Raise();
        }
    }

    public IReadOnlyList<Option<VpnPreset?>> VpnPresets =>
    [
        new(null, Loc.T("Vpn_PresetPick")),
        new(new VpnPreset(UpstreamProxyType.Socks5, 10808), "v2rayN — SOCKS 10808"),
        new(new VpnPreset(UpstreamProxyType.HttpConnect, 10809), "v2rayN — HTTP 10809"),
        new(new VpnPreset(UpstreamProxyType.Socks5, 7890), "Clash / Clash Verge / Mihomo — 7890"),
        new(new VpnPreset(UpstreamProxyType.Socks5, 2080), "Nekoray / NekoBox — 2080"),
        new(new VpnPreset(UpstreamProxyType.Socks5, 12334), "Hiddify — 12334"),
        new(new VpnPreset(UpstreamProxyType.Socks5, 1080), "SOCKS5 — 1080"),
    ];

    public VpnPreset? SelectedPreset
    {
        get => null;
        set
        {
            if (value is null)
                return;
            _upstream.Type = value.Type;
            _upstream.Host = "127.0.0.1";
            _upstream.Port = value.Port;
            Raise(nameof(Upstream));
            Raise(nameof(VpnType));
        }
    }

    public string VpnTestResult
    {
        get => _vpnTestResult;
        private set => Set(ref _vpnTestResult, value);
    }

    public bool VpnTestOk
    {
        get => _vpnTestOk;
        private set => Set(ref _vpnTestOk, value);
    }

    public ObservableCollection<Option<string>> WanOptions { get; } = [];
    public ObservableCollection<AdapterInfo> Adapters { get; } = [];

    public IReadOnlyList<Option<WifiBand>> Bands =>
    [
        new(WifiBand.Auto, Loc.T("Band_Auto")),
        new(WifiBand.TwoPointFourGHz, Loc.T("Band_24")),
        new(WifiBand.FiveGHz, Loc.T("Band_5")),
    ];

    public WifiSettings Wifi
    {
        get => _wifi;
        private set => Set(ref _wifi, value);
    }

    public int PortalPort
    {
        get => _portalPort;
        set => Set(ref _portalPort, value);
    }

    public bool IsolateHost
    {
        get => _isolateHost;
        set => Set(ref _isolateHost, value);
    }

    public bool OpenNetwork
    {
        get => _wifi.OpenNetwork;
        set
        {
            _wifi.OpenNetwork = value;
            Raise();
        }
    }

    public bool IsRunning => Services.Engine.State == GatewayState.Running;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public override void OnActivated()
    {
        LoadFromSettings();
        _ = RefreshAdaptersAsync();
    }

    public override void OnTick() => Raise(nameof(IsRunning));

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Raise(nameof(CoreStatus));
        foreach (var row in Servers)
            row.RefreshLabels();
        _ = RefreshAdaptersAsync();
    }

    private void LoadFromSettings()
    {
        var settings = Services.Settings.Snapshot();
        Wifi = settings.Wifi;
        PortalPort = settings.Portal.InternalPort;
        IsolateHost = settings.Portal.IsolateHost;
        Upstream = settings.Upstream;
        Servers.Clear();
        foreach (var server in _upstream.Servers)
            Servers.Add(new VpnServerRow(server));
        SelectedServer = Servers.FirstOrDefault(s => s.Profile.Id == _upstream.ActiveServerId) ?? Servers.FirstOrDefault();
        VpnTestResult = "";
        Message = "";
        Raise(nameof(VpnMode));
        Raise(nameof(IsCoreMode));
        Raise(nameof(IsExternalMode));
        Raise(nameof(VpnEnabled));
        Raise(nameof(CoreInstalled));
        Raise(nameof(CoreStatus));
        RaiseAll();
    }

    private void ImportLinks()
    {
        var parsed = ShareLinkParser.ParseMany(_importText);
        if (parsed.Count == 0)
        {
            Fail("Msg_ImportEmpty");
            return;
        }
        foreach (var server in parsed)
        {
            _upstream.Servers.Add(server);
            Servers.Add(new VpnServerRow(server));
        }
        SelectedServer ??= Servers.LastOrDefault();
        if (_upstream.Mode == UpstreamMode.Off)
            VpnMode = UpstreamMode.BuiltInCore;
        ImportText = "";
        MessageIsError = false;
        Message = Loc.Format("Msg_Imported", parsed.Count);
        Services.Log.Info($"Imported {parsed.Count} VPN server link(s).");
    }

    private void PasteAndImport()
    {
        try
        {
            if (Clipboard.ContainsText())
                ImportText = Clipboard.GetText();
        }
        catch
        {
            // Clipboard busy.
        }
        ImportLinks();
    }

    private void RemoveSelected()
    {
        if (_selectedServer is null)
            return;
        _upstream.Servers.Remove(_selectedServer.Profile);
        Servers.Remove(_selectedServer);
        SelectedServer = Servers.FirstOrDefault();
        _upstream.ActiveServerId = SelectedServer?.Profile.Id ?? "";
    }

    private async Task PingAllAsync()
    {
        VpnTestOk = false;
        VpnTestResult = Loc.T("Vpn_Pinging");
        var ok = 0;
        foreach (var row in Servers.ToList())
        {
            try
            {
                // Fast TCP RTT for the whole list; use Ping on a single server for a full Xray test.
                row.PingMs = await ShareLinkParser.TcpPingAsync(row.Profile);
                ok++;
            }
            catch
            {
                row.PingMs = -2;
            }
        }
        VpnTestOk = ok > 0;
        VpnTestResult = Loc.Format("Vpn_PingDone", ok, Servers.Count);
        var best = Servers.Where(s => s.PingMs >= 0).OrderBy(s => s.PingMs).FirstOrDefault();
        if (best is not null)
            SelectedServer = best;
    }

    private async Task PingSelectedAsync()
    {
        if (_selectedServer is null)
            return;
        VpnTestOk = false;
        VpnTestResult = Loc.T("Vpn_Pinging");
        try
        {
            var (tcp, proxy) = await Services.Engine.Xray.TestAsync(_selectedServer.Profile);
            _selectedServer.PingMs = proxy ?? tcp;
            if (_selectedServer.PingMs < 0)
            {
                VpnTestResult = Loc.T("Vpn_PingFail");
                return;
            }
            VpnTestOk = true;
            VpnTestResult = proxy is null
                ? Loc.Format("Vpn_PingTcpOnly", tcp)
                : Loc.Format("Vpn_PingOk", proxy, tcp);
        }
        catch (Exception ex)
        {
            _selectedServer.PingMs = -2;
            VpnTestResult = Loc.Format("Vpn_TestFailed", ex.Message);
        }
    }

    private async Task TestVpnAsync()
    {
        if (_upstream.Mode == UpstreamMode.BuiltInCore)
        {
            await PingSelectedAsync();
            return;
        }
        VpnTestOk = false;
        VpnTestResult = Loc.T("Vpn_Testing");
        try
        {
            var elapsed = await GatewayEngine.TestUpstreamAsync(_upstream);
            VpnTestOk = true;
            VpnTestResult = Loc.Format("Vpn_TestOk", (int)elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            VpnTestResult = Loc.Format("Vpn_TestFailed", ex.Message);
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        var (candidates, adapters) = await Task.Run(() => (HotspotManager.GetWanCandidates(), NetworkDiagnostics.GetAdapters()));
        var selected = _wifi.WanAdapterId;

        WanOptions.Clear();
        WanOptions.Add(new Option<string>("", Loc.T("Net_Auto")));
        foreach (var c in candidates)
        {
            var kind = c.IsEthernet ? "Ethernet" : c.IsWifi ? "Wi-Fi" : "";
            var share = c.CanShare ? Loc.T("Net_CanShare") : Loc.T("Net_CannotShare");
            WanOptions.Add(new Option<string>(c.AdapterId, $"{c.Name}  ·  {kind}  ·  {share}"));
        }
        if (!string.IsNullOrEmpty(selected) && WanOptions.All(o => o.Value != selected))
            WanOptions.Add(new Option<string>(selected, selected));

        Adapters.Clear();
        foreach (var adapter in adapters)
            Adapters.Add(adapter);

        _wifi.WanAdapterId = selected;
        Raise(nameof(Wifi));
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_wifi.Ssid) || _wifi.Ssid.Length > 32)
        {
            Fail("Msg_InvalidSsid");
            return;
        }
        if (_wifi.Passphrase.Length is < 8 or > 63)
        {
            Fail("Msg_InvalidPassphrase");
            return;
        }
        if (_portalPort is < 1024 or > 65535)
        {
            Fail("Msg_InvalidPort");
            return;
        }

        _upstream.Host = _upstream.Host.Trim();
        _upstream.Servers = Servers.Select(s => s.Profile).ToList();
        _upstream.ActiveServerId = SelectedServer?.Profile.Id ?? _upstream.Servers.FirstOrDefault()?.Id ?? "";

        if (_upstream.Mode == UpstreamMode.BuiltInCore)
        {
            if (!CoreInstalled)
            {
                Fail("Msg_CoreMissing");
                return;
            }
            if (_upstream.ActiveServer is null)
            {
                Fail("Msg_NoServer");
                return;
            }
        }
        else if (_upstream.Mode == UpstreamMode.ExternalPort)
        {
            if (_upstream.Host.Length == 0 || _upstream.Port is < 1 or > 65535)
            {
                Fail("Msg_InvalidVpn");
                return;
            }
        }

        if (_upstream.Mode != UpstreamMode.Off && (_upstream.RelayPort is < 1024 or > 65535 || _upstream.RelayPort == _portalPort))
        {
            Fail("Msg_InvalidRelayPort");
            return;
        }

        var settings = Services.Settings.Snapshot();
        settings.Wifi = _wifi;
        settings.Portal.InternalPort = _portalPort;
        settings.Portal.IsolateHost = _isolateHost;
        settings.Upstream = _upstream;
        Services.Settings.Save(settings);
        Services.Log.Info("Network settings saved.");
        var saved = Services.Settings.Snapshot();
        Wifi = saved.Wifi;
        Upstream = saved.Upstream;
        MessageIsError = false;
        Message = Loc.T(IsRunning ? "Net_RestartNotice" : "Msg_Saved");
    }

    private void Fail(string key)
    {
        MessageIsError = true;
        Message = Loc.T(key);
    }
}
