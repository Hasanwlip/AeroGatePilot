using System.Buffers.Binary;
using System.Text;

namespace AeroGatePilot.Core.Net;

public enum SniffStatus
{
    /// <summary>The site name was found.</summary>
    Found,

    /// <summary>The beginning of a TLS ClientHello or HTTP request was seen but more bytes are needed.</summary>
    NeedMore,

    /// <summary>Not TLS/HTTP, or the request carries no site name.</summary>
    Unknown,
}

/// <summary>Finds the site a TCP connection is for from its first bytes: TLS SNI or the HTTP Host header.</summary>
public static class StreamSniffer
{
    private static readonly string[] HttpMethods = ["GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "CONNECT ", "TRACE "];

    public static SniffStatus TryGetHost(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        if (data.IsEmpty)
            return SniffStatus.NeedMore;
        if (data[0] == 0x16)
            return data.Length < 2 || data[1] == 0x03 ? Tls(data, out host) : SniffStatus.Unknown;
        return Http(data, out host);
    }

    private static SniffStatus Tls(ReadOnlySpan<byte> d, out string? host)
    {
        host = null;
        if (d.Length < 9)
            return SniffStatus.NeedMore;
        if (d[5] != 0x01)
            return SniffStatus.Unknown;
        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(d[3..]);
        var handshakeLength = d[6] << 16 | d[7] << 8 | d[8];
        if (recordLength < handshakeLength + 4)
            return SniffStatus.Unknown;
        var end = 9 + handshakeLength;

        var p = 9 + 2 + 32;
        if (d.Length < p + 1)
            return SniffStatus.NeedMore;
        p += 1 + d[p];
        if (d.Length < p + 2)
            return SniffStatus.NeedMore;
        p += 2 + BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
        if (d.Length < p + 1)
            return SniffStatus.NeedMore;
        p += 1 + d[p];
        if (p + 2 > end)
            return SniffStatus.Unknown;
        if (d.Length < p + 2)
            return SniffStatus.NeedMore;
        var extensionsEnd = p + 2 + BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
        p += 2;
        if (extensionsEnd > end)
            return SniffStatus.Unknown;

        while (p + 4 <= extensionsEnd)
        {
            if (d.Length < p + 4)
                return SniffStatus.NeedMore;
            var type = BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]);
            p += 4;
            if (type == 0)
            {
                if (d.Length < p + length)
                    return SniffStatus.NeedMore;
                var q = p + 2;
                var listEnd = p + length;
                while (q + 3 <= listEnd)
                {
                    var nameType = d[q];
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(d[(q + 1)..]);
                    q += 3;
                    if (q + nameLength > listEnd)
                        return SniffStatus.Unknown;
                    if (nameType == 0)
                    {
                        host = Normalize(d.Slice(q, nameLength));
                        return host is null ? SniffStatus.Unknown : SniffStatus.Found;
                    }
                    q += nameLength;
                }
                return SniffStatus.Unknown;
            }
            p += length;
        }
        return SniffStatus.Unknown;
    }

    private static SniffStatus Http(ReadOnlySpan<byte> d, out string? host)
    {
        host = null;
        var isHttp = false;
        foreach (var method in HttpMethods)
        {
            var prefix = Math.Min(method.Length, d.Length);
            if (!Ascii(d[..prefix], method.AsSpan(0, prefix)))
                continue;
            if (prefix < method.Length)
                return SniffStatus.NeedMore;
            isHttp = true;
            break;
        }
        if (!isHttp)
            return SniffStatus.Unknown;

        var p = 0;
        var firstLine = true;
        while (true)
        {
            var rest = d[p..];
            var newline = rest.IndexOf((byte)'\n');
            if (newline < 0)
                return SniffStatus.NeedMore;
            var line = rest[..newline];
            if (!line.IsEmpty && line[^1] == '\r')
                line = line[..^1];
            p += newline + 1;
            if (firstLine)
            {
                firstLine = false;
                continue;
            }
            if (line.IsEmpty)
                return SniffStatus.Unknown;
            if (line.Length > 5 && Ascii(line[..5], "host:", ignoreCase: true))
            {
                host = Normalize(line[5..].Trim((byte)' ').Trim((byte)'\t'));
                return host is null ? SniffStatus.Unknown : SniffStatus.Found;
            }
        }
    }

    private static string? Normalize(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > 300)
            return null;
        var text = Encoding.ASCII.GetString(value);
        if (text.StartsWith('['))
            return null;
        var colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[..colon];
        return Filtering.DomainRules.NormalizeHost(text);
    }

    private static bool Ascii(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text, bool ignoreCase = false)
    {
        if (bytes.Length != text.Length)
            return false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var c = (char)bytes[i];
            if (ignoreCase)
                c = char.ToLowerInvariant(c);
            if (c != text[i])
                return false;
        }
        return true;
    }
}

public static class TcpReset
{
    /// <summary>
    /// Builds a TCP RST+ACK that appears to come from the server <paramref name="clientPacket"/> was sent to,
    /// so the client gives up the connection immediately instead of waiting for a timeout.
    /// Returns the number of bytes written (40), or 0 when the packet is not TCP.
    /// </summary>
    public static int BuildToClient(ReadOnlySpan<byte> clientPacket, Span<byte> output)
    {
        var copy = clientPacket.ToArray();
        if (!Ipv4Packet.TryParse(copy, out var ip) || ip.Protocol != IpProtocol.Tcp || !ip.HasPorts || output.Length < 40)
            return 0;

        var flags = ip.TcpFlags;
        var acknowledged = ip.TcpSequence + (uint)ip.TcpData.Length
            + ((flags & TcpFlag.Syn) != 0 ? 1u : 0u) + ((flags & TcpFlag.Fin) != 0 ? 1u : 0u);

        var o = output[..40];
        o.Clear();
        o[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(o[2..], 40);
        o[8] = 64;
        o[9] = IpProtocol.Tcp;
        BinaryPrimitives.WriteUInt32BigEndian(o[12..], ip.Destination);
        BinaryPrimitives.WriteUInt32BigEndian(o[16..], ip.Source);

        var tcp = o[20..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, ip.DestinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], ip.SourcePort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], (flags & TcpFlag.Ack) != 0 ? ip.TcpAcknowledgment : 0);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], acknowledged);
        tcp[12] = 5 << 4;
        tcp[13] = TcpFlag.Rst | TcpFlag.Ack;

        Ipv4Packet.TryParse(o, out var reset);
        reset.UpdateChecksums();
        return 40;
    }
}
