using System.Net;
using System.Runtime.InteropServices;
using AeroGatePilot.Core.Net;

namespace AeroGatePilot.Infrastructure.Network;

public sealed record ObservedClient(string Ip, string Mac, string? Username, bool LoggedIn);

/// <summary>Reads the Windows ARP/neighbor table so LAN clients show up before they sign in to the portal.</summary>
public static class LanNeighbors
{
    public static IReadOnlyList<(string Ip, string Mac)> List(Ipv4Subnet subnet)
    {
        var size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
                return [];
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 24;
            var list = new List<(string Ip, string Mac)>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * rowSize);
                var physLen = Marshal.ReadInt32(row, 4);
                if (physLen is < 6)
                    continue;
                var type = Marshal.ReadInt32(row, 20);
                if (type is not (3 or 4))
                    continue;
                var addr = unchecked((uint)Marshal.ReadInt32(row, 16));
                if (!subnet.Contains(addr))
                    continue;
                var mac = string.Create(17, (row, physLen), static (span, state) =>
                {
                    for (var b = 0; b < 6; b++)
                    {
                        if (b > 0)
                            span[b * 3 - 1] = ':';
                        var value = Marshal.ReadByte(state.row, 8 + b);
                        span[b * 3] = Hex(value >> 4);
                        span[b * 3 + 1] = Hex(value & 0xF);
                    }
                });
                if (mac == "00:00:00:00:00:00")
                    continue;
                list.Add((IpUtil.FromKey(addr).ToString(), mac.ToUpperInvariant()));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static char Hex(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);
}
