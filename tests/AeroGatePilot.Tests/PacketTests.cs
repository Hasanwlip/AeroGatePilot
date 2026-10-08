using System.Buffers.Binary;
using System.Net;
using System.Text;
using AeroGatePilot.Core.Net;

namespace AeroGatePilot.Tests;

public class PacketTests
{
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.137.23");
    private static readonly IPAddress GatewayIp = IPAddress.Parse("192.168.137.1");

    [Fact]
    public void Subnet_contains_and_bounds()
    {
        var subnet = Ipv4Subnet.From(GatewayIp, 24);
        Assert.Equal("192.168.137.0/24", subnet.ToString());
        Assert.True(subnet.Contains(IpUtil.ToKey(ClientIp)));
        Assert.False(subnet.Contains(IpUtil.ToKey(IPAddress.Parse("192.168.138.1"))));
        Assert.Equal("192.168.137.255", IpUtil.FromKey(subnet.Last).ToString());
    }

    [Fact]
    public void Dns_redirect_answers_a_query_with_gateway_address()
    {
        var query = BuildDnsQuery("connectivitycheck.gstatic.com", qtype: 1, id: 0x1234);
        var reply = new byte[1500];
        var length = DnsRedirect.BuildReply(query, IpUtil.ToKey(GatewayIp), reply);
        Assert.True(length > 0);

        Assert.True(Ipv4Packet.TryParse(reply.AsSpan(0, length), out var packet));
        Assert.Equal(IpProtocol.Udp, packet.Protocol);
        Assert.Equal(IpUtil.ToKey(GatewayIp), packet.Source);
        Assert.Equal(IpUtil.ToKey(ClientIp), packet.Destination);
        Assert.Equal(53, packet.SourcePort);
        Assert.Equal(50000, packet.DestinationPort);
        Assert.True(Checksum.Verify(packet));

        var dns = packet.Payload[8..];
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(dns));
        Assert.True((dns[2] & 0x80) != 0);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(dns[6..]));
        Assert.Equal("connectivitycheck.gstatic.com", DnsRedirect.ReadQuestionName(dns));
        var answerAddress = BinaryPrimitives.ReadUInt32BigEndian(dns[^4..]);
        Assert.Equal(IpUtil.ToKey(GatewayIp), answerAddress);
    }

    [Fact]
    public void Dns_redirect_returns_empty_answer_for_aaaa()
    {
        var query = BuildDnsQuery("example.com", qtype: 28, id: 7);
        var reply = new byte[1500];
        var length = DnsRedirect.BuildReply(query, IpUtil.ToKey(GatewayIp), reply);
        Assert.True(Ipv4Packet.TryParse(reply.AsSpan(0, length), out var packet));
        var dns = packet.Payload[8..];
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(dns[6..]));
    }

    [Fact]
    public void Non_dns_packets_are_ignored()
    {
        var reply = new byte[1500];
        Assert.Equal(0, DnsRedirect.BuildReply(new byte[10], 0, reply));
    }

    [Fact]
    public void Port_rewrite_keeps_checksums_valid()
    {
        var tcp = BuildTcpSyn(ClientIp, GatewayIp, 51515, 80);
        Assert.True(Ipv4Packet.TryParse(tcp, out var packet));
        Assert.True(Checksum.Verify(packet));
        packet.SetDestinationPort(8642);
        packet.UpdateChecksums();
        Assert.Equal(8642, packet.DestinationPort);
        Assert.True(Checksum.Verify(packet));
    }

    internal static byte[] BuildDnsQuery(string name, ushort qtype, ushort id)
    {
        var dns = new List<byte>();
        dns.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in name.Split('.'))
        {
            dns.Add((byte)label.Length);
            dns.AddRange(Encoding.ASCII.GetBytes(label));
        }
        dns.Add(0);
        dns.AddRange([(byte)(qtype >> 8), (byte)qtype, 0, 1]);

        var packet = new byte[20 + 8 + dns.Count];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = IpProtocol.Udp;
        ClientIp.GetAddressBytes().CopyTo(packet, 12);
        GatewayIp.GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + dns.Count));
        dns.CopyTo(packet, 28);
        Assert.True(Ipv4Packet.TryParse(packet, out var parsed));
        parsed.UpdateChecksums();
        return packet;
    }

    private static byte[] BuildTcpSyn(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort)
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
        packet[32] = 0x50;
        packet[33] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), 64240);
        Assert.True(Ipv4Packet.TryParse(packet, out var parsed));
        parsed.UpdateChecksums();
        return packet;
    }
}
