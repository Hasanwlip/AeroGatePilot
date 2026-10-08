using System.Runtime.InteropServices;

namespace AeroGatePilot.Infrastructure.Network;

internal enum WinDivertLayer : uint
{
    Network = 0,
    NetworkForward = 1,
}

[Flags]
internal enum WinDivertOpenFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
}

internal enum WinDivertShutdown : uint
{
    Recv = 1,
    Send = 2,
    Both = 3,
}

/// <summary>Mirror of WINDIVERT_ADDRESS (80 bytes) from WinDivert 2.2.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    private const int OutboundBit = 17;
    private const int ImpostorBit = 19;

    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(12)] public uint Reserved2;
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    public WinDivertLayer Layer
    {
        readonly get => (WinDivertLayer)(Bits & 0xFF);
        set => Bits = (Bits & ~0xFFu) | ((uint)value & 0xFF);
    }

    public bool Outbound
    {
        readonly get => (Bits & (1u << OutboundBit)) != 0;
        set => Bits = value ? Bits | (1u << OutboundBit) : Bits & ~(1u << OutboundBit);
    }

    public bool Impostor
    {
        readonly get => (Bits & (1u << ImpostorBit)) != 0;
        set => Bits = value ? Bits | (1u << ImpostorBit) : Bits & ~(1u << ImpostorBit);
    }
}

internal static class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public static readonly IntPtr InvalidHandle = new(-1);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr WinDivertOpen(string filter, WinDivertLayer layer, short priority, WinDivertOpenFlags flags);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool WinDivertRecv(IntPtr handle, byte* packet, uint packetLength, out uint receivedLength, ref WinDivertAddress address);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool WinDivertSend(IntPtr handle, byte* packet, uint packetLength, out uint sentLength, ref WinDivertAddress address);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertShutdown(IntPtr handle, WinDivertShutdown how);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool WinDivertHelperCalcChecksums(byte* packet, uint packetLength, ref WinDivertAddress address, ulong flags);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCompileFilter(string filter, WinDivertLayer layer, IntPtr obj, uint objLength, out IntPtr errorStr, out uint errorPos);

    public static bool IsAvailable(out string error)
    {
        var dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "WinDivert.dll")) || !File.Exists(Path.Combine(dir, "WinDivert64.sys")))
        {
            error = "WinDivert.dll / WinDivert64.sys are missing next to the application.";
            return false;
        }
        error = "";
        return true;
    }
}

/// <summary>Owns a WinDivert handle.</summary>
internal sealed class WinDivertHandle : IDisposable
{
    private IntPtr _handle;

    private WinDivertHandle(IntPtr handle) => _handle = handle;

    public static WinDivertHandle Open(string filter, WinDivertLayer layer, short priority = 0, WinDivertOpenFlags flags = WinDivertOpenFlags.None)
    {
        var handle = WinDivertNative.WinDivertOpen(filter, layer, priority, flags);
        if (handle == WinDivertNative.InvalidHandle || handle == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(code switch
            {
                5 => "WinDivert: access denied — run AeroGate Pilot as Administrator.",
                2 => "WinDivert: driver file WinDivert64.sys not found.",
                87 => $"WinDivert: invalid packet filter ({filter}).",
                577 => "WinDivert: driver signature rejected (Secure Boot / driver policy).",
                1275 => "WinDivert: driver blocked (incompatible WinDivert version already loaded or blocked by security software).",
                _ => $"WinDivert: open failed (Win32 error {code}).",
            });
        }
        return new WinDivertHandle(handle);
    }

    public unsafe bool Receive(byte[] buffer, out int length, ref WinDivertAddress address)
    {
        fixed (byte* p = buffer)
        {
            var ok = WinDivertNative.WinDivertRecv(_handle, p, (uint)buffer.Length, out var received, ref address);
            length = (int)received;
            return ok;
        }
    }

    public unsafe bool Send(byte[] buffer, int length, ref WinDivertAddress address, bool recalcChecksums = false)
    {
        fixed (byte* p = buffer)
        {
            if (recalcChecksums)
                WinDivertNative.WinDivertHelperCalcChecksums(p, (uint)length, ref address, 0);
            return WinDivertNative.WinDivertSend(_handle, p, (uint)length, out _, ref address);
        }
    }

    public void Shutdown()
    {
        if (_handle != IntPtr.Zero)
            WinDivertNative.WinDivertShutdown(_handle, WinDivertShutdown.Both);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            WinDivertNative.WinDivertClose(handle);
    }
}
