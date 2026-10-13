namespace AeroGatePilot.Core.Net;

/// <summary>Reads the client MAC and DHCP option 82 (relay agent) from a BOOTP/DHCP packet.</summary>
public static class DhcpMessage
{
    public readonly record struct RelayInfo(string Mac, string CircuitId, string RemoteId, string Gateway);

    public static bool TryParse(ReadOnlySpan<byte> udpPayload, out RelayInfo info)
    {
        info = default;
        if (udpPayload.Length < 240 || udpPayload[0] != 1 || udpPayload[2] == 0)
            return false;
        if (udpPayload[236] != 99 || udpPayload[237] != 130 || udpPayload[238] != 83 || udpPayload[239] != 99)
            return false;

        var macLen = Math.Min(udpPayload[2], (byte)16);
        var mac = new System.Text.StringBuilder(17);
        for (var i = 0; i < macLen && i < 6; i++)
        {
            if (i > 0)
                mac.Append(':');
            mac.Append(udpPayload[28 + i].ToString("X2"));
        }

        var circuit = "";
        var remote = "";
        var index = 240;
        while (index < udpPayload.Length)
        {
            var code = udpPayload[index++];
            if (code == 255)
                break;
            if (code == 0)
                continue;
            if (index >= udpPayload.Length)
                break;
            var len = udpPayload[index++];
            if (index + len > udpPayload.Length)
                break;
            if (code == 82)
                ReadOption82(udpPayload.Slice(index, len), ref circuit, ref remote);
            index += len;
        }

        var giaddr = $"{udpPayload[24]}.{udpPayload[25]}.{udpPayload[26]}.{udpPayload[27]}";
        if (giaddr == "0.0.0.0")
            giaddr = "";
        info = new RelayInfo(mac.ToString(), circuit, remote, giaddr);
        return mac.Length > 0;
    }

    public static string Label(RelayInfo info)
    {
        var parts = new[] { info.CircuitId, info.RemoteId, info.Gateway }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" | ", parts);
    }

    private static void ReadOption82(ReadOnlySpan<byte> data, ref string circuit, ref string remote)
    {
        var index = 0;
        while (index + 2 <= data.Length)
        {
            var sub = data[index++];
            var len = data[index++];
            if (index + len > data.Length)
                return;
            var text = Text(data.Slice(index, len));
            if (sub == 1)
                circuit = text;
            else if (sub == 2)
                remote = text;
            index += len;
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var printable = true;
        foreach (var b in bytes)
        {
            if (b is < 32 or > 126)
            {
                printable = false;
                break;
            }
        }
        if (printable && bytes.Length > 0)
            return System.Text.Encoding.ASCII.GetString(bytes);
        return Convert.ToHexString(bytes);
    }
}
