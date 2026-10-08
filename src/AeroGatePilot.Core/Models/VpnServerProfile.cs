namespace AeroGatePilot.Core.Models;

public enum VpnProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
}

public enum UpstreamMode
{
    /// <summary>Users go out on the PC's normal internet (no VPN).</summary>
    Off,

    /// <summary>Built-in Xray core next to the app — import VLESS / VMess / Trojan / SS links.</summary>
    BuiltInCore,

    /// <summary>An external VPN app's local SOCKS/HTTP port (Clash, v2rayN…).</summary>
    ExternalPort,
}

/// <summary>One VPN server imported from a share link.</summary>
public sealed class VpnServerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public VpnProtocol Protocol { get; set; } = VpnProtocol.Vless;
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Uuid { get; set; } = "";
    public string Password { get; set; } = "";
    public string Encryption { get; set; } = "none";
    public string Flow { get; set; } = "";
    public int AlterId { get; set; }
    public string Network { get; set; } = "tcp";
    public string Security { get; set; } = "none";
    public string Sni { get; set; } = "";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string SpiderX { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Mode { get; set; } = "";
    public string HeaderType { get; set; } = "";
    public string Seed { get; set; } = "";
    public string PacketEncoding { get; set; } = "";
    public string Extra { get; set; } = "";
    public bool AllowInsecure { get; set; }
    public string RawLink { get; set; } = "";

    /// <summary>Last measured TCP ping in ms; -1 = not tested, -2 = failed.</summary>
    public int LastPingMs { get; set; } = -1;

    public string ProtocolLabel => Protocol switch
    {
        VpnProtocol.Vless => "VLESS",
        VpnProtocol.Vmess => "VMess",
        VpnProtocol.Trojan => "Trojan",
        VpnProtocol.Shadowsocks => "SS",
        _ => Protocol.ToString(),
    };

    public string Endpoint => $"{Address}:{Port}";
}
