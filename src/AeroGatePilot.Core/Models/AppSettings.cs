namespace AeroGatePilot.Core.Models;

public enum PortalAuthMode
{
    UserPassword,
    GuestClickThrough,
    Both,
}

public enum WifiBand
{
    Auto,
    TwoPointFourGHz,
    FiveGHz,
}

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public WifiSettings Wifi { get; set; } = new();
    public PortalSettings Portal { get; set; } = new();
    public BrandingSettings Branding { get; set; } = new();
    public UpstreamProxySettings Upstream { get; set; } = new();
}

public enum UpstreamProxyType
{
    Socks5,
    HttpConnect,
}

public enum UpstreamProxyScope
{
    /// <summary>Only plans with "Route through VPN" turned on.</summary>
    SelectedPlans,
    AllUsers,
}

/// <summary>
/// How Wi-Fi users reach the internet through this PC: direct, built-in Xray core, or an external VPN app port.
/// </summary>
public sealed class UpstreamProxySettings
{
    public UpstreamMode Mode { get; set; } = UpstreamMode.Off;

    /// <summary>Legacy flag kept for older settings.json files.</summary>
    public bool Enabled
    {
        get => Mode != UpstreamMode.Off;
        set
        {
            if (!value)
                Mode = UpstreamMode.Off;
            else if (Mode == UpstreamMode.Off)
                Mode = UpstreamMode.ExternalPort;
        }
    }

    public UpstreamProxyType Type { get; set; } = UpstreamProxyType.Socks5;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10808;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public UpstreamProxyScope Scope { get; set; } = UpstreamProxyScope.AllUsers;

    /// <summary>Connect directly when the VPN port does not answer, instead of failing the connection.</summary>
    public bool FallbackDirect { get; set; }

    /// <summary>Local port of the transparent relay on the hotspot address.</summary>
    public int RelayPort { get; set; } = 8643;

    /// <summary>Local SOCKS port used by the built-in Xray core.</summary>
    public int CoreSocksPort { get; set; } = 18680;

    /// <summary>Id of the selected share-link server when <see cref="Mode"/> is <see cref="UpstreamMode.BuiltInCore"/>.</summary>
    public string ActiveServerId { get; set; } = "";

    public List<VpnServerProfile> Servers { get; set; } = [];

    public VpnServerProfile? ActiveServer =>
        Servers.FirstOrDefault(s => s.Id == ActiveServerId) ?? Servers.FirstOrDefault();
}

public sealed class WifiSettings
{
    public string Ssid { get; set; } = "AeroGate";
    public string Passphrase { get; set; } = "";
    public WifiBand Band { get; set; } = WifiBand.Auto;

    /// <summary>
    /// Requested open (password-less) network. Windows Mobile Hotspot always enforces WPA2,
    /// so this is only honoured when the platform reports support for it.
    /// </summary>
    public bool OpenNetwork { get; set; }

    /// <summary>Network adapter id (GUID) of the internet (WAN) connection to share. Empty = auto.</summary>
    public string WanAdapterId { get; set; } = "";
}

public sealed class PortalSettings
{
    public PortalAuthMode AuthMode { get; set; } = PortalAuthMode.UserPassword;
    public long GuestPlanId { get; set; }
    public int InternalPort { get; set; } = 8642;
    public string SuccessRedirectUrl { get; set; } = "";

    /// <summary>Block Wi-Fi clients from reaching services on this PC (file shares, local web servers, RDP...).</summary>
    public bool IsolateHost { get; set; } = true;
}

public sealed class BrandingSettings
{
    public string Title { get; set; } = "AeroGate Wi-Fi";
    public string TitleFa { get; set; } = "وای‌فای ایروگیت";
    public string Subtitle { get; set; } = "Sign in to get online";
    public string SubtitleFa { get; set; } = "برای اتصال به اینترنت وارد شوید";
    public string Terms { get; set; } = "By connecting you agree to use this network responsibly.";
    public string TermsFa { get; set; } = "با اتصال به این شبکه، استفاده مسئولانه از آن را می‌پذیرید.";
    public string Footer { get; set; } = "Powered by AeroGate Pilot";
    public string PrimaryColor { get; set; } = "#6C5CE7";
    public string AccentColor { get; set; } = "#00CEC9";
    public string LogoFile { get; set; } = "";
    public string BackgroundFile { get; set; } = "";
    public string CustomCss { get; set; } = "";
    public string DefaultLanguage { get; set; } = "en";
    public bool ShowLanguageSwitch { get; set; } = true;
}
