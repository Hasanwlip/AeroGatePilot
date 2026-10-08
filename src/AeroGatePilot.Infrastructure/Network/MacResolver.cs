using System.Net;
using System.Runtime.InteropServices;

namespace AeroGatePilot.Infrastructure.Network;

public static class MacResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint macAddressLength);

    /// <summary>Resolves the MAC address of a device on the local link, or returns an empty string.</summary>
    public static string Resolve(IPAddress address)
    {
        try
        {
            var bytes = address.MapToIPv4().GetAddressBytes();
            var ip = BitConverter.ToUInt32(bytes, 0);
            var mac = new byte[6];
            uint length = (uint)mac.Length;
            if (SendARP(ip, 0, mac, ref length) != 0 || length == 0)
                return "";
            return string.Join(":", mac.Take((int)length).Select(b => b.ToString("X2")));
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static string Normalize(string mac) =>
        mac.Replace('-', ':').ToUpperInvariant();
}
