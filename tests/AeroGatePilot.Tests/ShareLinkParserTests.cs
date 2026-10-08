using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Proxy;

namespace AeroGatePilot.Tests;

public class ShareLinkParserTests
{
    [Fact]
    public void Parses_vless_reality_vision()
    {
        var link = "vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.cloudflare.com&fp=chrome&pbk=PUBLICKEY&sid=abcd1234&type=tcp#MyServer";
        Assert.True(ShareLinkParser.TryParse(link, out var p));
        Assert.NotNull(p);
        Assert.Equal(VpnProtocol.Vless, p!.Protocol);
        Assert.Equal("MyServer", p.Name);
        Assert.Equal("example.com", p.Address);
        Assert.Equal(443, p.Port);
        Assert.Equal("11111111-2222-3333-4444-555555555555", p.Uuid);
        Assert.Equal("xtls-rprx-vision", p.Flow);
        Assert.Equal("reality", p.Security);
        Assert.Equal("www.cloudflare.com", p.Sni);
        Assert.Equal("chrome", p.Fingerprint);
        Assert.Equal("PUBLICKEY", p.PublicKey);
        Assert.Equal("abcd1234", p.ShortId);
    }

    [Fact]
    public void Parses_vless_ws_tls()
    {
        var link = "vless://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee@cdn.example.net:443?type=ws&security=tls&path=%2Fapi%2F&host=cdn.example.net&sni=cdn.example.net&fp=chrome#WS";
        Assert.True(ShareLinkParser.TryParse(link, out var p));
        Assert.Equal("ws", p!.Network);
        Assert.Equal("/api/", p.Path);
        Assert.Equal("tls", p.Security);
    }

    [Fact]
    public void Parses_trojan_and_ss()
    {
        Assert.True(ShareLinkParser.TryParse("trojan://secret@host.example:443?security=tls&sni=host.example#T1", out var trojan));
        Assert.Equal(VpnProtocol.Trojan, trojan!.Protocol);
        Assert.Equal("secret", trojan.Password);

        var user = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pass123"));
        Assert.True(ShareLinkParser.TryParse($"ss://{user}@1.2.3.4:8388#SS1", out var ss));
        Assert.Equal(VpnProtocol.Shadowsocks, ss!.Protocol);
        Assert.Equal("aes-256-gcm", ss.Encryption);
        Assert.Equal("pass123", ss.Password);
        Assert.Equal("1.2.3.4", ss.Address);
    }

    [Fact]
    public void Parses_many_lines()
    {
        var text = """
            vless://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee@a.com:443?encryption=none&security=tls&type=tcp#A
            # comment
            trojan://x@b.com:443?security=tls#B
            """;
        var list = ShareLinkParser.ParseMany(text);
        Assert.Equal(2, list.Count);
    }
}
