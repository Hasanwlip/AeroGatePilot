using System.Buffers.Binary;
using System.Text;

namespace AeroGatePilot.Core.Net;

public static class IpProtocol
{
    public const byte Icmp = 1;
    public const byte Tcp = 6;
    public const byte Udp = 17;
}

public static class TcpFlag
{
    public const byte Fin = 0x01;
    public const byte Syn = 0x02;
    public const byte Rst = 0x04;
    public const byte Ack = 0x10;
}

/// <summary>Minimal, allocation-free view over an IPv4 packet held in a byte buffer.</summary>
public readonly ref struct Ipv4Packet
{
    private readonly Span<byte> _data;

    private Ipv4Packet(Span<byte> data) => _data = data;

    public static bool TryParse(Span<byte> data, out Ipv4Packet packet)
    {
        packet = default;
        if (data.Length < 20 || data[0] >> 4 != 4)
            return false;
        var headerLength = (data[0] & 0x0F) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (headerLength < 20 || totalLength < headerLength || totalLength > data.Length)
            return false;
        packet = new Ipv4Packet(data[..totalLength]);
        return true;
    }

    public Span<byte> Data => _data;
    public int HeaderLength => (_data[0] & 0x0F) * 4;
    public int TotalLength => _data.Length;
    public byte Protocol => _data[9];
    public uint Source => BinaryPrimitives.ReadUInt32BigEndian(_data[12..]);
    public uint Destination => BinaryPrimitives.ReadUInt32BigEndian(_data[16..]);

    /// <summary>True for the first (or only) fragment, which is the one carrying transport headers.</summary>
    public bool IsFirstFragment => (BinaryPrimitives.ReadUInt16BigEndian(_data[6..]) & 0x1FFF) == 0;

    public Span<byte> Payload => _data[HeaderLength..];

    public bool HasPorts => (Protocol == IpProtocol.Tcp && Payload.Length >= 20 || Protocol == IpProtocol.Udp && Payload.Length >= 8) && IsFirstFragment;
    public ushort SourcePort => BinaryPrimitives.ReadUInt16BigEndian(Payload);
    public ushort DestinationPort => BinaryPrimitives.ReadUInt16BigEndian(Payload[2..]);

    public void SetSourcePort(ushort port) => BinaryPrimitives.WriteUInt16BigEndian(Payload, port);
    public void SetDestinationPort(ushort port) => BinaryPrimitives.WriteUInt16BigEndian(Payload[2..], port);
    public void SetSource(uint address) => BinaryPrimitives.WriteUInt32BigEndian(_data[12..], address);
    public void SetDestination(uint address) => BinaryPrimitives.WriteUInt32BigEndian(_data[16..], address);

    public uint TcpSequence => BinaryPrimitives.ReadUInt32BigEndian(Payload[4..]);
    public uint TcpAcknowledgment => BinaryPrimitives.ReadUInt32BigEndian(Payload[8..]);
    public byte TcpFlags => Payload[13];
    public int TcpHeaderLength => Math.Min((Payload[12] >> 4) * 4, Payload.Length);
    public Span<byte> TcpData => Payload[TcpHeaderLength..];
    public Span<byte> UdpData => Payload[8..];

    /// <summary>Recomputes the IPv4 header checksum and the TCP/UDP checksum.</summary>
    public void UpdateChecksums()
    {
        var header = _data[..HeaderLength];
        header[10] = 0;
        header[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], Checksum.Compute(header));

        var payload = Payload;
        var offset = Protocol switch
        {
            IpProtocol.Tcp when payload.Length >= 20 => 16,
            IpProtocol.Udp when payload.Length >= 8 => 6,
            _ => -1,
        };
        if (offset < 0 || !IsFirstFragment)
            return;

        payload[offset] = 0;
        payload[offset + 1] = 0;
        var sum = Checksum.PseudoHeaderSum(_data[12..16], _data[16..20], Protocol, payload.Length);
        var value = Checksum.Finish(Checksum.Sum(payload, sum));
        if (Protocol == IpProtocol.Udp && value == 0)
            value = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(payload[offset..], value);
    }
}

public static class Checksum
{
    public static uint Sum(ReadOnlySpan<byte> data, uint initial = 0)
    {
        var sum = initial;
        var i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        return sum;
    }

    public static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public static ushort Compute(ReadOnlySpan<byte> data) => Finish(Sum(data));

    public static uint PseudoHeaderSum(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, byte protocol, int length)
    {
        var sum = Sum(source);
        sum = Sum(destination, sum);
        sum += protocol;
        sum += (uint)length;
        return sum;
    }

    /// <summary>Returns true when the packet's IPv4 and transport checksums are valid.</summary>
    public static bool Verify(Ipv4Packet packet)
    {
        if (Compute(packet.Data[..packet.HeaderLength]) != 0)
            return false;
        if (packet.Protocol is not (IpProtocol.Tcp or IpProtocol.Udp))
            return true;
        var payload = packet.Payload;
        var sum = PseudoHeaderSum(packet.Data[12..16], packet.Data[16..20], packet.Protocol, payload.Length);
        return Finish(Sum(payload, sum)) == 0;
    }
}

public static class DnsRedirect
{
    private const ushort TypeA = 1;

    /// <summary>
    /// Builds a UDP/IPv4 DNS response to <paramref name="query"/> that answers every A question with
    /// <paramref name="answerAddress"/> (TTL 1s). Other record types get an empty NOERROR answer.
    /// Returns the number of bytes written to <paramref name="output"/>, or 0 if the packet is not a usable DNS query.
    /// </summary>
    public static int BuildReply(ReadOnlySpan<byte> query, uint answerAddress, Span<byte> output) =>
        Build(query, answerAddress, 0, output);

    /// <summary>Builds an NXDOMAIN ("no such site") response to <paramref name="query"/>.</summary>
    public static int BuildNxDomain(ReadOnlySpan<byte> query, Span<byte> output) =>
        Build(query, null, 3, output);

    /// <summary>Builds a NOERROR response without answers ("this name has no such record").</summary>
    public static int BuildEmpty(ReadOnlySpan<byte> query, Span<byte> output) =>
        Build(query, null, 0, output);

    private static int Build(ReadOnlySpan<byte> query, uint? answerAddress, byte responseCode, Span<byte> output)
    {
        var copy = query.ToArray();
        if (!Ipv4Packet.TryParse(copy, out var ip) || ip.Protocol != IpProtocol.Udp || !ip.HasPorts)
            return 0;

        var udp = ip.Payload;
        var dns = udp[8..];
        if (dns.Length < 12 || (dns[2] & 0x80) != 0)
            return 0;
        if (BinaryPrimitives.ReadUInt16BigEndian(dns[4..]) != 1)
            return 0;

        var pos = 12;
        while (true)
        {
            if (pos >= dns.Length)
                return 0;
            int labelLength = dns[pos];
            if (labelLength == 0)
            {
                pos++;
                break;
            }
            if ((labelLength & 0xC0) != 0)
                return 0;
            pos += labelLength + 1;
        }
        if (pos + 4 > dns.Length)
            return 0;
        var qtype = BinaryPrimitives.ReadUInt16BigEndian(dns[pos..]);
        var questionEnd = pos + 4;
        var answer = qtype == TypeA && answerAddress.HasValue;

        var dnsLength = questionEnd + (answer ? 16 : 0);
        var total = 20 + 8 + dnsLength;
        if (output.Length < total)
            return 0;

        var o = output[..total];
        o.Clear();
        o[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(o[2..], (ushort)total);
        o[8] = 64;
        o[9] = IpProtocol.Udp;
        ip.Data[16..20].CopyTo(o[12..]);
        ip.Data[12..16].CopyTo(o[16..]);

        var oUdp = o[20..];
        BinaryPrimitives.WriteUInt16BigEndian(oUdp, ip.DestinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(oUdp[2..], ip.SourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(oUdp[4..], (ushort)(8 + dnsLength));

        var oDns = oUdp[8..];
        dns[..questionEnd].CopyTo(oDns);
        var recursionDesired = (byte)(dns[2] & 0x01);
        oDns[2] = (byte)(0x80 | 0x04 | recursionDesired);
        oDns[3] = (byte)(0x80 | responseCode);
        BinaryPrimitives.WriteUInt16BigEndian(oDns[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(oDns[6..], (ushort)(answer ? 1 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(oDns[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(oDns[10..], 0);

        if (answer)
        {
            var a = oDns[questionEnd..];
            a[0] = 0xC0;
            a[1] = 0x0C;
            BinaryPrimitives.WriteUInt16BigEndian(a[2..], TypeA);
            BinaryPrimitives.WriteUInt16BigEndian(a[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(a[6..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(a[10..], 4);
            BinaryPrimitives.WriteUInt32BigEndian(a[12..], answerAddress!.Value);
        }

        Ipv4Packet.TryParse(o, out var reply);
        reply.UpdateChecksums();
        return total;
    }

    /// <summary>Extracts the queried host name from a DNS query packet (for diagnostics/tests).</summary>
    public static string? ReadQuestionName(ReadOnlySpan<byte> dnsMessage)
    {
        if (dnsMessage.Length < 13)
            return null;
        var sb = new StringBuilder();
        var pos = 12;
        while (pos < dnsMessage.Length && dnsMessage[pos] != 0)
        {
            int len = dnsMessage[pos];
            if (pos + 1 + len > dnsMessage.Length)
                return null;
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(dnsMessage.Slice(pos + 1, len)));
            pos += len + 1;
        }
        return sb.ToString();
    }
}
