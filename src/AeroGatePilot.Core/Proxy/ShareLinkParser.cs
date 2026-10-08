using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Proxy;

/// <summary>
/// Parses share links used by v2rayN / Hiddify / Nekoray: <c>vless://</c>, <c>vmess://</c>, <c>trojan://</c>, <c>ss://</c>.
/// </summary>
public static class ShareLinkParser
{
    public static IReadOnlyList<VpnServerProfile> ParseMany(string text)
    {
        var list = new List<VpnServerProfile>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        foreach (var raw in text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(raw.Trim(), out var profile) && profile is not null)
                list.Add(profile);
        }
        return list;
    }

    public static bool TryParse(string link, out VpnServerProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(link))
            return false;
        link = link.Trim();
        try
        {
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                profile = ParseVless(link);
            else if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                profile = ParseVmess(link);
            else if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                profile = ParseTrojan(link);
            else if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                profile = ParseShadowsocks(link);
            else
                return false;
            return profile is not null;
        }
        catch
        {
            return false;
        }
    }

    private static VpnServerProfile ParseVless(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (string.IsNullOrWhiteSpace(name))
            name = uri.IdnHost;
        return new VpnServerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Protocol = VpnProtocol.Vless,
            Address = uri.IdnHost,
            Port = uri.Port > 0 ? uri.Port : 443,
            Uuid = uri.UserInfo,
            Encryption = Q(q, "encryption", "none"),
            Flow = Q(q, "flow"),
            Network = NormNetwork(Q(q, "type", "tcp")),
            Security = NormSecurity(Q(q, "security", "none")),
            Sni = Q(q, "sni", Q(q, "host")),
            Host = Q(q, "host"),
            Path = Uri.UnescapeDataString(Q(q, "path")),
            Fingerprint = Q(q, "fp"),
            PublicKey = Q(q, "pbk"),
            ShortId = Q(q, "sid"),
            SpiderX = Uri.UnescapeDataString(Q(q, "spx")),
            Alpn = Q(q, "alpn"),
            ServiceName = Q(q, "serviceName"),
            Mode = Q(q, "mode"),
            AllowInsecure = Q(q, "allowInsecure") is "1" or "true",
            PacketEncoding = Q(q, "packetEncoding"),
            HeaderType = Q(q, "headerType"),
            Seed = Q(q, "seed"),
            Extra = Q(q, "extra"),
            RawLink = link,
        };
    }

    private static VpnServerProfile ParseVmess(string link)
    {
        var payload = link["vmess://".Length..];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(payload)));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string a, string b = "") => r.TryGetProperty(a, out var v) ? v.ToString() : b;
        var port = r.TryGetProperty("port", out var p)
            ? p.ValueKind == JsonValueKind.Number ? p.GetInt32() : int.Parse(p.GetString()!)
            : 443;
        var tls = S("tls");
        return new VpnServerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(S("ps")) ? S("add") : S("ps"),
            Protocol = VpnProtocol.Vmess,
            Address = S("add"),
            Port = port,
            Uuid = S("id"),
            AlterId = int.TryParse(S("aid", "0"), out var aid) ? aid : 0,
            Encryption = string.IsNullOrWhiteSpace(S("scy")) ? "auto" : S("scy"),
            Network = NormNetwork(S("net", "tcp")),
            Security = tls.Equals("tls", StringComparison.OrdinalIgnoreCase) ? "tls"
                : tls.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "reality" : "none",
            Sni = S("sni"),
            Host = S("host"),
            Path = S("path"),
            Fingerprint = S("fp"),
            Alpn = S("alpn"),
            HeaderType = S("type"),
            AllowInsecure = S("allowInsecure") is "1" or "true",
            RawLink = link,
        };
    }

    private static VpnServerProfile ParseTrojan(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (string.IsNullOrWhiteSpace(name))
            name = uri.IdnHost;
        return new VpnServerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Protocol = VpnProtocol.Trojan,
            Address = uri.IdnHost,
            Port = uri.Port > 0 ? uri.Port : 443,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Network = NormNetwork(Q(q, "type", "tcp")),
            Security = NormSecurity(Q(q, "security", "tls")),
            Sni = Q(q, "sni", Q(q, "host", uri.IdnHost)),
            Host = Q(q, "host"),
            Path = Uri.UnescapeDataString(Q(q, "path")),
            Fingerprint = Q(q, "fp"),
            PublicKey = Q(q, "pbk"),
            ShortId = Q(q, "sid"),
            Alpn = Q(q, "alpn"),
            AllowInsecure = Q(q, "allowInsecure") is "1" or "true",
            RawLink = link,
        };
    }

    private static VpnServerProfile ParseShadowsocks(string link)
    {
        var hash = "";
        var body = link["ss://".Length..];
        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
        {
            hash = Uri.UnescapeDataString(body[(hashIdx + 1)..]);
            body = body[..hashIdx];
        }

        string method, password, host;
        int port;
        if (body.Contains('@'))
        {
            var at = body.LastIndexOf('@');
            var userPart = body[..at];
            var user = userPart.Contains(':')
                ? userPart
                : Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(userPart)));
            var hp = body[(at + 1)..];
            var colon = user.IndexOf(':');
            method = user[..colon];
            password = user[(colon + 1)..];
            var hostPort = SplitHostPort(hp);
            host = hostPort.Host;
            port = hostPort.Port;
        }
        else
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(body)));
            var at = decoded.LastIndexOf('@');
            var user = decoded[..at];
            var hp = decoded[(at + 1)..];
            var colon = user.IndexOf(':');
            method = user[..colon];
            password = user[(colon + 1)..];
            var hostPort = SplitHostPort(hp);
            host = hostPort.Host;
            port = hostPort.Port;
        }

        return new VpnServerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(hash) ? host : hash,
            Protocol = VpnProtocol.Shadowsocks,
            Address = host,
            Port = port,
            Encryption = method,
            Password = password,
            Network = "tcp",
            Security = "none",
            RawLink = link,
        };
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        var colon = value.LastIndexOf(':');
        return (value[..colon], int.Parse(value[(colon + 1)..]));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                map[Uri.UnescapeDataString(part)] = "";
            else
                map[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return map;
    }

    private static string Q(Dictionary<string, string> q, string key, string fallback = "") =>
        q.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static string PadBase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static string NormNetwork(string n) => n.ToLowerInvariant() switch
    {
        "h2" or "http" => "h2",
        "websocket" => "ws",
        "xhttp" or "splithttp" => "xhttp",
        var x => x,
    };

    private static string NormSecurity(string s) => s.ToLowerInvariant() switch
    {
        "xtls" => "tls",
        "" => "none",
        var x => x,
    };

    /// <summary>Quick TCP connect RTT to the server address (does not verify the VPN handshake).</summary>
    public static async Task<int> TcpPingAsync(VpnServerProfile server, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(server.Address, server.Port, timeout.Token);
        return (int)watch.ElapsedMilliseconds;
    }
}
