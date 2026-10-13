using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AeroGatePilot.Infrastructure.Network;

public sealed record LanCandidate(string AdapterId, string Name, string Description, string Ipv4, bool IsUp)
{
    public override string ToString() => Name;
}

public sealed record IcsShareSnapshot(string PublicAdapterId, string PrivateAdapterId);

/// <summary>
/// Enables Windows Internet Connection Sharing from a WAN NIC to a LAN NIC (switch / MikroTik / APs)
/// via the HNetCfg COM API — the same ICS stack Mobile Hotspot uses.
/// </summary>
public sealed class IcsShareManager
{
    private const int SharingPublic = 0;
    private const int SharingPrivate = 1;

    private IcsShareSnapshot? _active;

    public IcsShareSnapshot? Active => _active;

    public static IReadOnlyList<LanCandidate> GetLanCandidates(string? excludeWanAdapterId = null)
    {
        var exclude = NormalizeGuid(excludeWanAdapterId);
        var list = new List<LanCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                continue;
            // Prefer real Ethernet / USB Ethernet for LAN out; allow other non-WAN adapters as fallback.
            var id = NormalizeGuid(nic.Id);
            if (id.Length == 0 || (exclude.Length > 0 && id.Equals(exclude, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211))
                continue;

            var ipv4 = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "";
            list.Add(new LanCandidate(id, nic.Name, nic.Description, ipv4, nic.OperationalStatus == OperationalStatus.Up));
        }

        return list
            .OrderByDescending(c => c.IsUp)
            .ThenBy(c => c.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Name)
            .ToList();
    }

    public static string? ResolveWanAdapterId(string? configuredId)
    {
        var profile = HotspotManager.FindProfile(configuredId ?? "");
        return profile?.NetworkAdapter is null ? null : NormalizeGuid(profile.NetworkAdapter.NetworkAdapterId.ToString());
    }

    public void Start(string wanAdapterId, string lanAdapterId, AppLog log)
    {
        var wan = NormalizeGuid(wanAdapterId);
        var lan = NormalizeGuid(lanAdapterId);
        if (wan.Length == 0)
            throw new InvalidOperationException("Select the internet (WAN) adapter to share.");
        if (lan.Length == 0)
            throw new InvalidOperationException("Select the LAN adapter that connects to the switch / MikroTik.");
        if (wan.Equals(lan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WAN and LAN adapters must be different.");

        var lanNic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => NormalizeGuid(n.Id).Equals(lan, StringComparison.OrdinalIgnoreCase));
        if (lanNic is null)
            throw new InvalidOperationException("The selected LAN adapter was not found.");
        if (lanNic.OperationalStatus != OperationalStatus.Up)
            throw new InvalidOperationException($"LAN adapter \"{lanNic.Name}\" is not connected. Plug the cable to the switch / MikroTik.");

        foreach (var id in IcsRepair.ClearSharingEntries())
            log.Info($"Cleared previous sharing entry for adapter {{{id}}}.");

        try
        {
            IcsRepair.RestartSharingService();
        }
        catch (Exception ex)
        {
            log.Warn($"Could not restart SharedAccess service: {ex.Message}");
        }

        EnablePair(wan, lan);
        _active = new IcsShareSnapshot(wan, lan);
        log.Info($"Internet Connection Sharing: WAN {{{wan}}} → LAN {{{lan}}}.");
    }

    public void Stop(AppLog log)
    {
        if (_active is null)
            return;
        try
        {
            DisableAll();
            log.Info("Internet Connection Sharing on LAN stopped.");
        }
        catch (Exception ex)
        {
            log.Error("Stopping LAN Internet Connection Sharing", ex);
        }
        finally
        {
            _active = null;
        }
    }

    private static void EnablePair(string publicGuid, string privateGuid)
    {
        var manager = CreateManager();
        try
        {
            object? publicConn = null;
            object? privateConn = null;
            foreach (var conn in EnumConnections(manager))
            {
                var guid = ConnectionGuid(manager, conn);
                if (guid.Equals(publicGuid, StringComparison.OrdinalIgnoreCase))
                    publicConn = conn;
                else if (guid.Equals(privateGuid, StringComparison.OrdinalIgnoreCase))
                    privateConn = conn;
            }

            if (publicConn is null)
                throw new InvalidOperationException("WAN adapter is not visible to Windows Internet Connection Sharing.");
            if (privateConn is null)
                throw new InvalidOperationException("LAN adapter is not visible to Windows Internet Connection Sharing.");

            // Disable any existing share first, then enable public then private.
            foreach (var conn in EnumConnections(manager))
            {
                var config = SharingConfig(manager, conn);
                if (IsSharingEnabled(config))
                    Invoke(config, "DisableSharing");
            }

            var publicConfig = SharingConfig(manager, publicConn);
            Invoke(publicConfig, "EnableSharing", SharingPublic);
            var privateConfig = SharingConfig(manager, privateConn);
            Invoke(privateConfig, "EnableSharing", SharingPrivate);
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static void DisableAll()
    {
        var manager = CreateManager();
        try
        {
            foreach (var conn in EnumConnections(manager))
            {
                var config = SharingConfig(manager, conn);
                if (IsSharingEnabled(config))
                    Invoke(config, "DisableSharing");
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static object CreateManager()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.HNetShare")
                   ?? throw new InvalidOperationException("Windows Internet Connection Sharing (HNetCfg) is not available on this PC.");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("Could not create the Internet Connection Sharing manager.");
    }

    private static IEnumerable<object> EnumConnections(object manager)
    {
        var every = Get(manager, "EnumEveryConnection")
                    ?? throw new InvalidOperationException("Could not enumerate network connections for sharing.");
        foreach (var item in (System.Collections.IEnumerable)every)
        {
            if (item is not null)
                yield return item;
        }
    }

    private static string ConnectionGuid(object manager, object connection)
    {
        var props = GetIndex(manager, "NetConnectionProps", connection)
                    ?? throw new InvalidOperationException("Could not read connection properties.");
        return NormalizeGuid(Get(props, "Guid") as string);
    }

    private static object SharingConfig(object manager, object connection) =>
        GetIndex(manager, "INetSharingConfigurationForINetConnection", connection)
        ?? throw new InvalidOperationException("Could not open sharing configuration for an adapter.");

    private static bool IsSharingEnabled(object config) =>
        Get(config, "SharingEnabled") is true;

    private const System.Reflection.BindingFlags GetFlags =
        System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

    private const System.Reflection.BindingFlags CallFlags =
        System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, GetFlags, null, target, null);

    private static object? GetIndex(object target, string name, object index) =>
        target.GetType().InvokeMember(name, GetFlags, null, target, [index]);

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, CallFlags, null, target, args.Length == 0 ? null : args);

    public static string NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Trim().Trim('{', '}').ToUpperInvariant();
    }
}
