using System.Collections.ObjectModel;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Infrastructure;

namespace AeroGatePilot.App.ViewModels;

public sealed class SessionRow : ObservableObject
{
    public SessionRow(ClientSession session, Action<SessionRow> kick)
    {
        Session = session;
        KickCommand = new RelayCommand(() => kick(this));
    }

    public ClientSession Session { get; }
    public RelayCommand KickCommand { get; }

    public string Username => Session.User.IsGuest ? $"{Loc.T("Mode_Guest")}" : Session.User.Username;
    public string DisplayName => Session.User.DisplayName;
    public string Ip => IpUtil.Format(Session.Ip);
    public string Mac => string.IsNullOrEmpty(Session.Mac) ? "—" : Session.Mac;
    public string Plan => Session.Plan.Name;
    public string Started => Session.StartedUtc.ToLocalTime().ToString("HH:mm");
    public string Download => Formatting.Bytes(Session.DownloadBytes);
    public string Upload => Formatting.Bytes(Session.UploadBytes);
    public string Speed => $"↓ {Formatting.Rate(Session.DownloadRateBps)}   ↑ {Formatting.Rate(Session.UploadRateBps)}";

    public void Refresh() => RaiseAll();
}

public sealed class DashboardViewModel : PageViewModel
{
    private const int HistoryLength = 120;
    private readonly Queue<double> _downHistory = new(Enumerable.Repeat(0d, HistoryLength));
    private readonly Queue<double> _upHistory = new(Enumerable.Repeat(0d, HistoryLength));

    public DashboardViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Key => "dashboard";

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    private GatewayEngine Engine => Services.Engine;

    public bool IsRunning => Engine.State == GatewayState.Running;
    public string Ssid => Services.Settings.Current.Wifi.Ssid;
    public string Gateway => Engine.GatewayAddress is { } gw ? $"{gw}  ·  {Engine.Subnet}" : "—";
    public string Wan => string.IsNullOrEmpty(Engine.WanName) ? "—" : Engine.WanName;
    public string Uptime => Engine.StartedUtc is { } started ? Formatting.Duration(DateTime.UtcNow - started) : "—";
    public string PortalUrl => Engine.GatewayAddress is { } gw ? $"http://{gw}/portal/" : "—";
    public string LastError => Engine.State == GatewayState.Faulted ? Engine.LastError : "";

    public int OnlineCount => Engine.Sessions.Count;
    public int DeviceCount => IsRunning ? Engine.HotspotClientCount : 0;
    public string DownloadRate { get; private set; } = Formatting.Rate(0);
    public string UploadRate { get; private set; } = Formatting.Rate(0);

    public long Forwarded => Engine.FilterStats?.Forwarded ?? 0;
    public long Blocked => Engine.FilterStats?.BlockedUnauthorized ?? 0;
    public long DnsRedirects => Engine.FilterStats?.DnsRedirected ?? 0;
    public long PortalHits => Engine.FilterStats?.PortalRequests ?? 0;
    public long RateLimited => Engine.FilterStats?.RateLimited ?? 0;
    public long HostBlocked => Engine.FilterStats?.HostBlocked ?? 0;
    public long SitesBlocked => Engine.FilterStats?.SitesBlocked ?? 0;
    public long VpnConnections => Engine.FilterStats?.Proxied ?? 0;
    public bool VpnActive => Engine.VpnRelayActive;

    public double[] DownSeries { get; private set; } = new double[HistoryLength];
    public double[] UpSeries { get; private set; } = new double[HistoryLength];

    /// <summary>Records throughput every second, even when another page is visible.</summary>
    public void Sample()
    {
        var sessions = Engine.Sessions.Sessions;
        var down = sessions.Sum(s => s.DownloadRateBps);
        var up = sessions.Sum(s => s.UploadRateBps);
        Push(_downHistory, down);
        Push(_upHistory, up);
        DownloadRate = Formatting.Rate(down);
        UploadRate = Formatting.Rate(up);
    }

    public override void OnActivated() => OnTick();

    public override void OnTick()
    {
        DownSeries = _downHistory.ToArray();
        UpSeries = _upHistory.ToArray();
        SyncSessions();
        RaiseAll();
    }

    private void SyncSessions()
    {
        var live = Engine.Sessions.Sessions;
        foreach (var row in Sessions.Where(r => !live.Contains(r.Session)).ToList())
            Sessions.Remove(row);
        foreach (var session in live)
        {
            var row = Sessions.FirstOrDefault(r => ReferenceEquals(r.Session, session));
            if (row is null)
                Sessions.Add(new SessionRow(session, Kick));
            else
                row.Refresh();
        }
    }

    private void Kick(SessionRow row)
    {
        Engine.KickDevice(row.Session);
        OnTick();
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistoryLength)
            queue.Dequeue();
    }
}
