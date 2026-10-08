using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AeroGatePilot.Core.Net;

public static class IpUtil
{
    /// <summary>IPv4 address as a host-order integer, matching the byte order read from packet headers.</summary>
    public static uint ToKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static IPAddress FromKey(uint key)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, key);
        return new IPAddress(bytes);
    }

    public static uint PrefixMask(int prefixLength) =>
        prefixLength <= 0 ? 0u : prefixLength >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);

    public static string Format(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    public static string Format(uint key) => FromKey(key).ToString();

    /// <summary>RFC 1918, CGNAT, link-local and loopback ranges.</summary>
    public static bool IsPrivate(uint key) =>
        (key & 0xFF000000) == 0x0A000000
        || (key & 0xFFF00000) == 0xAC100000
        || (key & 0xFFFF0000) == 0xC0A80000
        || (key & 0xFFC00000) == 0x64400000
        || (key & 0xFFFF0000) == 0xA9FE0000
        || (key & 0xFF000000) == 0x7F000000;
}

/// <summary>An IPv4 subnet, e.g. 192.168.137.0/24.</summary>
public readonly record struct Ipv4Subnet(uint Network, int PrefixLength)
{
    public uint Mask => IpUtil.PrefixMask(PrefixLength);
    public uint First => Network & Mask;
    public uint Last => First | ~Mask;

    public bool Contains(uint address) => (address & Mask) == First;

    public static Ipv4Subnet From(IPAddress address, int prefixLength)
    {
        var mask = IpUtil.PrefixMask(prefixLength);
        return new Ipv4Subnet(IpUtil.ToKey(address) & mask, prefixLength);
    }

    public override string ToString() => $"{IpUtil.FromKey(First)}/{PrefixLength}";
}
