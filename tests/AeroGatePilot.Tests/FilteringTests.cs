using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Core.Proxy;

namespace AeroGatePilot.Tests;

public class FilteringTests
{
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.137.23");
    private static readonly IPAddress ServerIp = IPAddress.Parse("142.250.1.1");

    // ---------- Domain rules ----------

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", false)]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", true)]
    [InlineData("*.example.com", "badexample.com", false)]
    [InlineData("*.example.com", "example.com.evil.net", false)]
    [InlineData("https://WWW.Example.com/path?q=1", "www.example.com", true)]
    [InlineData("example.com:8443", "EXAMPLE.COM.", true)]
    public void Domain_rules_match_exact_names_and_wildcard_branches(string rule, string host, bool expected) =>
        Assert.Equal(expected, DomainRules.Parse(rule).Matches(host));

    [Fact]
    public void Domain_rules_accept_lines_commas_and_comments()
    {
        var rules = DomainRules.Parse("""
            # social
            *.instagram.com, twitter.com
            youtube.com ; *.googlevideo.com
            not a valid host!
            """);
        Assert.False(rules.Matches("scontent.cdninstagram.com"));
        Assert.True(rules.Matches("i.instagram.com"));
        Assert.True(rules.Matches("twitter.com"));
        Assert.True(rules.Matches("rr3.googlevideo.com"));
        Assert.Contains("host!", DomainRules.InvalidEntries("ok.com host!"));
    }

    [Fact]
    public void Block_list_blocks_listed_sites_only()
    {
        var plan = new Plan { SiteFilter = SiteFilterMode.BlockListed, SiteList = "*.instagram.com\ntelegram.org" };
        Assert.False(SitePolicy.AllowsHost(plan, "www.instagram.com"));
        Assert.False(SitePolicy.AllowsHost(plan, "telegram.org"));
        Assert.True(SitePolicy.AllowsHost(plan, "web.telegram.org"));
        Assert.True(SitePolicy.AllowsHost(plan, "google.com"));
        Assert.False(SitePolicy.AllowsHost(plan, "dns.google"));
        Assert.True(SitePolicy.AllowsAddress(plan, []));
        Assert.False(SitePolicy.AllowsAddress(plan, ["cdn.example.net", "i.instagram.com"]));
    }

    [Fact]
    public void Allow_list_allows_listed_sites_and_connectivity_checks_only()
    {
        var plan = new Plan { SiteFilter = SiteFilterMode.AllowListed, SiteList = "*.company.com" };
        Assert.True(SitePolicy.AllowsHost(plan, "company.com"));
        Assert.True(SitePolicy.AllowsHost(plan, "mail.company.com"));
        Assert.False(SitePolicy.AllowsHost(plan, "google.com"));
        Assert.True(SitePolicy.AllowsHost(plan, "captive.apple.com"));
        Assert.False(SitePolicy.AllowsHost(plan, "mask.icloud.com"));
        Assert.False(SitePolicy.AllowsAddress(plan, []));
        Assert.True(SitePolicy.AllowsAddress(plan, ["portal.company.com"]));
    }

    [Fact]
    public void Off_allows_everything()
    {
        var plan = new Plan { SiteList = "*.anything.com" };
        Assert.False(SitePolicy.IsFiltered(plan));
        Assert.True(SitePolicy.AllowsHost(plan, "x.anything.com"));
    }

    [Fact]
    public void Host_cache_remembers_names_until_expiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new HostAddressCache(() => now);
        cache.Add(0x01020304, "WWW.Example.com.", 30);
        cache.Add(0x01020304, "example.com", 30);
        Assert.Equal(["www.example.com", "example.com"], cache.NamesFor(0x01020304));
        now = now.AddHours(3);
        Assert.Empty(cache.NamesFor(0x01020304));
    }

    // ---------- DNS ----------

    [Fact]
    public void Dns_answers_map_addresses_to_the_question_through_cnames()
    {
        var response = BuildDnsResponse("www.site.com", "site.cdn.net", 0x08080404, 120);
        Assert.True(DnsMessage.TryReadQuestion(response, out var name, out var type));
        Assert.Equal("www.site.com", name);
        Assert.Equal(DnsMessage.TypeA, type);

        var records = DnsMessage.ReadAddresses(response);
        Assert.Contains(records, r => r.Name == "site.cdn.net" && r.Address == 0x08080404 && r.Ttl == 120);
        Assert.Contains(records, r => r.Name == "www.site.com" && r.Address == 0x08080404);
    }

    [Fact]
    public void Nxdomain_reply_answers_the_client()
    {
        var query = PacketTests.BuildDnsQuery("blocked.example", qtype: 1, id: 9);
        var reply = new byte[1500];
        var length = DnsRedirect.BuildNxDomain(query, reply);
        Assert.True(Ipv4Packet.TryParse(reply.AsSpan(0, length), out var packet));
        Assert.True(Checksum.Verify(packet));
        var dns = packet.UdpData;
        Assert.Equal(3, dns[3] & 0x0F);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(dns[6..]));
        Assert.Equal(IpUtil.ToKey(ClientIp), packet.Destination);
    }

    // ---------- Site name sniffing ----------

    [Fact]
    public async Task Sni_is_read_from_a_real_tls_client_hello()
    {
        var hello = await CaptureClientHelloAsync("secure.example.org");
        Assert.Equal(SniffStatus.Found, StreamSniffer.TryGetHost(hello, out var host));
        Assert.Equal("secure.example.org", host);

        Assert.Equal(SniffStatus.NeedMore, StreamSniffer.TryGetHost(hello.AsSpan(0, 60), out _));
        Assert.Equal(SniffStatus.NeedMore, StreamSniffer.TryGetHost(hello.AsSpan(0, 3), out _));
    }

    [Fact]
    public void Http_host_header_is_read()
    {
        var request = "GET /index.html HTTP/1.1\r\nUser-Agent: x\r\nHOST: Shop.Example.net:8080\r\n\r\n"u8.ToArray();
        Assert.Equal(SniffStatus.Found, StreamSniffer.TryGetHost(request, out var host));
        Assert.Equal("shop.example.net", host);
        Assert.Equal(SniffStatus.NeedMore, StreamSniffer.TryGetHost(request.AsSpan(0, 30), out _));
        Assert.Equal(SniffStatus.NeedMore, StreamSniffer.TryGetHost("GE"u8, out _));
        Assert.Equal(SniffStatus.Unknown, StreamSniffer.TryGetHost("SSH-2.0-OpenSSH\r\n"u8, out _));
        Assert.Equal(SniffStatus.Unknown, StreamSniffer.TryGetHost("GET / HTTP/1.0\r\n\r\n"u8, out _));
    }

    [Fact]
    public void Reset_looks_like_it_comes_from_the_server()
    {
        var syn = BuildTcp(ClientIp, ServerIp, 50123, 443, seq: 1000, ack: 0, flags: TcpFlag.Syn);
        var reply = new byte[64];
        Assert.Equal(40, TcpReset.BuildToClient(syn, reply));
        Assert.True(Ipv4Packet.TryParse(reply.AsSpan(0, 40), out var rst));
        Assert.True(Checksum.Verify(rst));
        Assert.Equal(IpUtil.ToKey(ServerIp), rst.Source);
        Assert.Equal(IpUtil.ToKey(ClientIp), rst.Destination);
        Assert.Equal(443, rst.SourcePort);
        Assert.Equal(50123, rst.DestinationPort);
        Assert.Equal(1001u, rst.TcpAcknowledgment);
        Assert.Equal(TcpFlag.Rst | TcpFlag.Ack, rst.TcpFlags);
    }

    // ---------- Upstream connectors ----------

    [Fact]
    public async Task Socks5_connector_sends_the_site_name_to_the_proxy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var s = socket.GetStream();
            var greeting = new byte[3];
            await s.ReadExactlyAsync(greeting);
            Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
            await s.WriteAsync(new byte[] { 5, 0 });
            var head = new byte[5];
            await s.ReadExactlyAsync(head);
            Assert.Equal(3, head[3]);
            var rest = new byte[head[4] + 2];
            await s.ReadExactlyAsync(rest);
            var target = Encoding.ASCII.GetString(rest, 0, head[4]);
            var targetPort = BinaryPrimitives.ReadUInt16BigEndian(rest.AsSpan(head[4]));
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await s.WriteAsync(Encoding.ASCII.GetBytes($"{target}:{targetPort}"));
        });

        await using var stream = await new Socks5Connector("127.0.0.1", port).ConnectAsync("www.example.com", 443, CancellationToken.None);
        var buffer = new byte[64];
        var read = await stream.ReadAtLeastAsync(buffer, 19);
        Assert.Equal("www.example.com:443", Encoding.ASCII.GetString(buffer, 0, read));
        await server;
    }

    [Fact]
    public async Task Http_connect_connector_tunnels_after_200()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var s = socket.GetStream();
            var reader = new StreamReader(s, Encoding.ASCII, false, 1, leaveOpen: true);
            var first = await reader.ReadLineAsync();
            Assert.Equal("CONNECT api.example.com:443 HTTP/1.1", first);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }
            await s.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\nhello"u8.ToArray());
        });

        await using var stream = await new HttpConnectConnector("127.0.0.1", port).ConnectAsync("api.example.com", 443, CancellationToken.None);
        var buffer = new byte[5];
        await stream.ReadExactlyAsync(buffer);
        Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
        await server;
    }

    [Fact]
    public async Task Socks5_connector_reports_a_wrong_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await socket.GetStream().WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());
        });
        await Assert.ThrowsAsync<IOException>(() => new Socks5Connector("127.0.0.1", port).ConnectAsync("example.com", 80, CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Upstream_stream_stays_open_after_garbage_collection()
    {
        // Returning tcpClient.GetStream() alone lets the GC close the socket; OwnedTcpStream must prevent that.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var s = socket.GetStream();
            var buf = new byte[4];
            await s.ReadExactlyAsync(buf);
            await Task.Delay(200);
            await s.WriteAsync(buf);
        });

        var stream = await new DirectConnector().ConnectAsync("127.0.0.1", port, CancellationToken.None);
        await stream.WriteAsync("ping"u8.ToArray());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var echo = new byte[4];
        await stream.ReadExactlyAsync(echo);
        Assert.Equal("ping"u8.ToArray(), echo);
        await stream.DisposeAsync();
        await server;
    }

    // ---------- Helpers ----------

    private static async Task<byte[]> CaptureClientHelloAsync(string serverName)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        var handshake = ssl.AuthenticateAsClientAsync(serverName);

        var buffer = new byte[16 * 1024];
        var length = 0;
        var stream = accepted.GetStream();
        while (length < 5 || length < 5 + BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(3)))
            length += await stream.ReadAsync(buffer.AsMemory(length));

        accepted.Close();
        try
        {
            await handshake;
        }
        catch
        {
            // The fake server never answers.
        }
        return buffer[..length];
    }

    private static byte[] BuildDnsResponse(string question, string cname, uint address, uint ttl)
    {
        var dns = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0 };
        AddName(dns, question);
        dns.AddRange([0, 1, 0, 1]);

        dns.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60]);
        var cnameData = new List<byte>();
        AddName(cnameData, cname);
        dns.AddRange([(byte)(cnameData.Count >> 8), (byte)cnameData.Count]);
        var cnameOffset = dns.Count;
        dns.AddRange(cnameData);

        dns.AddRange([(byte)(0xC0 | cnameOffset >> 8), (byte)cnameOffset, 0, 1, 0, 1]);
        dns.AddRange([(byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl, 0, 4]);
        dns.AddRange([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);
        return [.. dns];
    }

    private static void AddName(List<byte> dns, string name)
    {
        foreach (var label in name.Split('.'))
        {
            dns.Add((byte)label.Length);
            dns.AddRange(Encoding.ASCII.GetBytes(label));
        }
        dns.Add(0);
    }

    private static byte[] BuildTcp(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort, uint seq, uint ack, byte flags)
    {
        var packet = new byte[40];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 40);
        packet[8] = 64;
        packet[9] = IpProtocol.Tcp;
        src.GetAddressBytes().CopyTo(packet, 12);
        dst.GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28), ack);
        packet[32] = 0x50;
        packet[33] = flags;
        Assert.True(Ipv4Packet.TryParse(packet, out var parsed));
        parsed.UpdateChecksums();
        return packet;
    }
}
