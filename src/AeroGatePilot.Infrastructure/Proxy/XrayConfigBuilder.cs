using System.Text.Json;
using System.Text.Json.Nodes;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Infrastructure.Proxy;

/// <summary>Builds a minimal Xray client config: local SOCKS inbound + one outbound from a share-link profile.</summary>
public static class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Build(VpnServerProfile server, int socksPort) =>
        JsonSerializer.Serialize(BuildNode(server, socksPort), JsonOptions);

    public static JsonObject BuildNode(VpnServerProfile server, int socksPort)
    {
        var outbound = BuildOutbound(server);
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["udp"] = true,
                        ["auth"] = "noauth",
                    },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls", "quic"),
                        ["routeOnly"] = true,
                    },
                },
            },
            ["outbounds"] = new JsonArray
            {
                outbound,
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            },
            // Keep routing simple so a missing/unreadable geoip.dat cannot block startup.
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray(),
            },
        };
    }

    private static JsonObject BuildOutbound(VpnServerProfile s)
    {
        var o = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = s.Protocol switch
            {
                VpnProtocol.Vless => "vless",
                VpnProtocol.Vmess => "vmess",
                VpnProtocol.Trojan => "trojan",
                VpnProtocol.Shadowsocks => "shadowsocks",
                _ => "vless",
            },
            ["settings"] = BuildSettings(s),
            ["streamSettings"] = BuildStream(s),
        };
        return o;
    }

    private static JsonObject VlessUser(VpnServerProfile s)
    {
        var user = new JsonObject
        {
            ["id"] = s.Uuid,
            ["encryption"] = string.IsNullOrWhiteSpace(s.Encryption) ? "none" : s.Encryption,
        };
        if (!string.IsNullOrWhiteSpace(s.Flow))
            user["flow"] = s.Flow;
        return user;
    }

    private static JsonObject BuildSettings(VpnServerProfile s) => s.Protocol switch
    {
        VpnProtocol.Vless => new JsonObject
        {
            ["vnext"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = s.Address,
                    ["port"] = s.Port,
                    ["users"] = new JsonArray { VlessUser(s) },
                },
            },
        },
        VpnProtocol.Vmess => new JsonObject
        {
            ["vnext"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = s.Address,
                    ["port"] = s.Port,
                    ["users"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = s.Uuid,
                            ["alterId"] = s.AlterId,
                            ["security"] = string.IsNullOrWhiteSpace(s.Encryption) ? "auto" : s.Encryption,
                        },
                    },
                },
            },
        },
        VpnProtocol.Trojan => new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = s.Address,
                    ["port"] = s.Port,
                    ["password"] = s.Password,
                },
            },
        },
        VpnProtocol.Shadowsocks => new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = s.Address,
                    ["port"] = s.Port,
                    ["method"] = s.Encryption,
                    ["password"] = s.Password,
                },
            },
        },
        _ => new JsonObject(),
    };

    private static JsonObject BuildStream(VpnServerProfile s)
    {
        var stream = new JsonObject
        {
            ["network"] = string.IsNullOrWhiteSpace(s.Network) ? "tcp" : s.Network,
            ["security"] = string.IsNullOrWhiteSpace(s.Security) ? "none" : s.Security,
        };

        if (s.Security is "tls" or "reality")
        {
            var tls = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(s.Sni) ? s.Address : s.Sni,
                ["allowInsecure"] = s.AllowInsecure,
            };
            if (!string.IsNullOrWhiteSpace(s.Fingerprint))
                tls["fingerprint"] = s.Fingerprint;
            if (!string.IsNullOrWhiteSpace(s.Alpn))
                tls["alpn"] = new JsonArray(s.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => (JsonNode)a).ToArray());

            if (s.Security == "reality")
            {
                tls["publicKey"] = s.PublicKey;
                tls["shortId"] = s.ShortId ?? "";
                if (!string.IsNullOrWhiteSpace(s.SpiderX))
                    tls["spiderX"] = s.SpiderX;
                stream["realitySettings"] = tls;
            }
            else
            {
                stream["tlsSettings"] = tls;
            }
        }

        switch (s.Network)
        {
            case "ws":
                stream["wsSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path,
                    ["headers"] = string.IsNullOrWhiteSpace(s.Host)
                        ? new JsonObject()
                        : new JsonObject { ["Host"] = s.Host },
                };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = s.ServiceName ?? "",
                };
                break;
            case "h2":
                stream["httpSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path,
                    ["host"] = string.IsNullOrWhiteSpace(s.Host)
                        ? new JsonArray()
                        : new JsonArray(s.Host),
                };
                break;
            case "xhttp":
                var xhttp = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path,
                };
                if (!string.IsNullOrWhiteSpace(s.Host))
                    xhttp["host"] = s.Host;
                if (!string.IsNullOrWhiteSpace(s.Mode))
                    xhttp["mode"] = s.Mode;
                if (!string.IsNullOrWhiteSpace(s.Extra))
                {
                    try
                    {
                        xhttp["extra"] = JsonNode.Parse(s.Extra);
                    }
                    catch
                    {
                        // keep without extra
                    }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "tcp" when !string.IsNullOrWhiteSpace(s.HeaderType) && s.HeaderType != "none":
                stream["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["type"] = s.HeaderType },
                };
                break;
        }

        return stream;
    }
}
