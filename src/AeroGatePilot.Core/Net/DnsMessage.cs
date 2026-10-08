using System.Buffers.Binary;
using System.Text;

namespace AeroGatePilot.Core.Net;

/// <summary>Reads the parts of DNS messages the gateway needs: the question and IPv4 answers.</summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeCname = 5;
    public const ushort TypeHttps = 65;

    public readonly record struct AddressRecord(string Name, uint Address, uint Ttl);

    public static bool IsResponse(ReadOnlySpan<byte> dns) => dns.Length >= 12 && (dns[2] & 0x80) != 0;

    public static bool TryReadQuestion(ReadOnlySpan<byte> dns, out string name, out ushort type)
    {
        name = "";
        type = 0;
        if (dns.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(dns[4..]) == 0)
            return false;
        var pos = 12;
        var sb = new StringBuilder();
        if (!TryReadName(dns, ref pos, sb) || pos + 4 > dns.Length)
            return false;
        name = sb.ToString();
        type = BinaryPrimitives.ReadUInt16BigEndian(dns[pos..]);
        return name.Length > 0;
    }

    /// <summary>
    /// Returns every A record of a response together with the question name, so CNAME chains
    /// (www.site.com → site.cdn.net → 1.2.3.4) map the address back to the name the client asked for.
    /// </summary>
    public static List<AddressRecord> ReadAddresses(ReadOnlySpan<byte> dns)
    {
        var result = new List<AddressRecord>();
        if (!IsResponse(dns) || (dns[3] & 0x0F) != 0)
            return result;

        var questions = BinaryPrimitives.ReadUInt16BigEndian(dns[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(dns[6..]);
        var pos = 12;
        var sb = new StringBuilder();
        string? questionName = null;
        for (var i = 0; i < questions; i++)
        {
            sb.Clear();
            if (!TryReadName(dns, ref pos, sb) || pos + 4 > dns.Length)
                return result;
            questionName ??= sb.ToString();
            pos += 4;
        }

        for (var i = 0; i < answers; i++)
        {
            sb.Clear();
            if (!TryReadName(dns, ref pos, sb) || pos + 10 > dns.Length)
                return result;
            var type = BinaryPrimitives.ReadUInt16BigEndian(dns[pos..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(dns[(pos + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(dns[(pos + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(dns[(pos + 8)..]);
            pos += 10;
            if (pos + length > dns.Length)
                return result;
            if (type == TypeA && cls == 1 && length == 4)
            {
                var address = BinaryPrimitives.ReadUInt32BigEndian(dns[pos..]);
                var owner = sb.ToString();
                result.Add(new AddressRecord(owner, address, ttl));
                if (questionName is not null && !string.Equals(owner, questionName, StringComparison.OrdinalIgnoreCase))
                    result.Add(new AddressRecord(questionName, address, ttl));
            }
            pos += length;
        }
        return result;
    }

    /// <summary>Reads a (possibly compressed) domain name starting at <paramref name="pos"/>, advancing past it.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> dns, ref int pos, StringBuilder sb)
    {
        var cursor = pos;
        var jumped = false;
        for (var guard = 0; guard < 128; guard++)
        {
            if (cursor >= dns.Length)
                return false;
            int length = dns[cursor];
            if (length == 0)
            {
                if (!jumped)
                    pos = cursor + 1;
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= dns.Length)
                    return false;
                if (!jumped)
                    pos = cursor + 2;
                jumped = true;
                cursor = (length & 0x3F) << 8 | dns[cursor + 1];
                continue;
            }
            if ((length & 0xC0) != 0 || cursor + 1 + length > dns.Length)
                return false;
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(dns.Slice(cursor + 1, length)));
            if (sb.Length > 255)
                return false;
            cursor += length + 1;
        }
        return false;
    }
}
