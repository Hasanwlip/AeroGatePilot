using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Core.Proxy;

/// <summary>
/// Opens a TCP stream to <c>host:port</c> on behalf of a hotspot client. Implementations: direct, SOCKS5, HTTP CONNECT
/// (and later protocol clients such as VLESS that carry the stream themselves).
/// </summary>
public interface IUpstreamConnector
{
    string Description { get; }
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

public static class UpstreamConnector
{
    public static IUpstreamConnector Create(UpstreamProxySettings settings) => settings.Type switch
    {
        UpstreamProxyType.HttpConnect => new HttpConnectConnector(settings.Host, settings.Port, settings.Username, settings.Password),
        _ => new Socks5Connector(settings.Host, settings.Port, settings.Username, settings.Password),
    };

    /// <summary>Fetches a tiny page through <paramref name="connector"/> and returns the round-trip time.</summary>
    public static async Task<TimeSpan> TestAsync(IUpstreamConnector connector, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var stream = await connector.ConnectAsync("cp.cloudflare.com", 80, cancellationToken);
        var request = "GET / HTTP/1.1\r\nHost: cp.cloudflare.com\r\nUser-Agent: AeroGatePilot\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(request, cancellationToken);
        var buffer = new byte[64];
        var read = await stream.ReadAtLeastAsync(buffer, 12, throwOnEndOfStream: false, cancellationToken);
        var status = Encoding.ASCII.GetString(buffer, 0, read);
        if (!status.StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new IOException("The VPN port answered, but no web page came back through it.");
        return watch.Elapsed;
    }

    internal static async Task<TcpClient> OpenTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new IOException("The VPN port closed the connection during the handshake.");
        }
    }
}

public sealed class DirectConnector : IUpstreamConnector
{
    public string Description => "direct";

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = await UpstreamConnector.OpenTcpAsync(host, port, cancellationToken);
        return new OwnedTcpStream(client);
    }
}

/// <summary>SOCKS5 (RFC 1928) with optional username/password authentication (RFC 1929). Host names are resolved by the proxy.</summary>
public sealed class Socks5Connector(string proxyHost, int proxyPort, string username = "", string password = "") : IUpstreamConnector
{
    public string Description => $"SOCKS5 {proxyHost}:{proxyPort}";

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = await UpstreamConnector.OpenTcpAsync(proxyHost, proxyPort, cancellationToken);
        var stream = client.GetStream();
        try
        {
            var useAuth = username.Length > 0;
            await stream.WriteAsync(useAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, cancellationToken);
            var choice = new byte[2];
            await UpstreamConnector.ReadExactlyAsync(stream, choice, cancellationToken);
            if (choice[0] != 5)
                throw new IOException("The VPN port is not a SOCKS5 proxy. Check the proxy type and port.");
            if (choice[1] == 2)
            {
                var user = Encoding.UTF8.GetBytes(username);
                var pass = Encoding.UTF8.GetBytes(password);
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, cancellationToken);
                var authReply = new byte[2];
                await UpstreamConnector.ReadExactlyAsync(stream, authReply, cancellationToken);
                if (authReply[1] != 0)
                    throw new IOException("The SOCKS5 proxy rejected the username or password.");
            }
            else if (choice[1] != 0)
            {
                throw new IOException("The SOCKS5 proxy requires authentication that is not configured.");
            }

            byte[] request;
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                request = new byte[10];
                request[3] = 1;
                ip.GetAddressBytes().CopyTo(request, 4);
            }
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                request = new byte[7 + name.Length];
                request[3] = 3;
                request[4] = (byte)name.Length;
                name.CopyTo(request, 5);
            }
            request[0] = 5;
            request[1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(request.Length - 2), (ushort)port);
            await stream.WriteAsync(request, cancellationToken);

            var head = new byte[4];
            await UpstreamConnector.ReadExactlyAsync(stream, head, cancellationToken);
            if (head[1] != 0)
                throw new IOException($"The SOCKS5 proxy could not reach {host}:{port} (code {head[1]}).");
            var rest = head[3] switch
            {
                1 => 4 + 2,
                4 => 16 + 2,
                3 => -1,
                _ => throw new IOException("Invalid SOCKS5 reply."),
            };
            if (rest < 0)
            {
                var len = new byte[1];
                await UpstreamConnector.ReadExactlyAsync(stream, len, cancellationToken);
                rest = len[0] + 2;
            }
            await UpstreamConnector.ReadExactlyAsync(stream, new byte[rest], cancellationToken);
            return new OwnedTcpStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>HTTP proxy using the CONNECT method, with optional Basic authentication.</summary>
public sealed class HttpConnectConnector(string proxyHost, int proxyPort, string username = "", string password = "") : IUpstreamConnector
{
    public string Description => $"HTTP {proxyHost}:{proxyPort}";

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = await UpstreamConnector.OpenTcpAsync(proxyHost, proxyPort, cancellationToken);
        var stream = client.GetStream();
        try
        {
            var sb = new StringBuilder();
            sb.Append($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
            if (username.Length > 0)
                sb.Append($"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}\r\n");
            sb.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), cancellationToken);

            // Read the response header byte by byte so no tunnelled data is consumed.
            var header = new List<byte>(128);
            var one = new byte[1];
            while (header.Count < 8192)
            {
                await UpstreamConnector.ReadExactlyAsync(stream, one, cancellationToken);
                header.Add(one[0]);
                var n = header.Count;
                if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n')
                    break;
            }
            var text = Encoding.ASCII.GetString(header.ToArray());
            var parts = text.Split(' ', 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal))
                throw new IOException("The VPN port is not an HTTP proxy. Check the proxy type and port.");
            if (parts[1] != "200")
                throw new IOException($"The HTTP proxy refused {host}:{port} ({text.Split('\r')[0]}).");
            return new OwnedTcpStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
