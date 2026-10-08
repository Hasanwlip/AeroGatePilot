using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace AeroGatePilot.Infrastructure.Network;

/// <summary>
/// Detects and repairs a broken Windows Internet Connection Sharing state. When an adapter that no longer exists
/// (typically an uninstalled VPN/TAP adapter) is still flagged as the ICS public or private connection, Mobile Hotspot
/// turns on but ICS never starts its DHCP/DNS server, so phones join the Wi-Fi without ever getting an IP address.
/// </summary>
public static partial class IcsRepair
{
    private const string HomeNetScope = @"\\.\root\Microsoft\HomeNet";

    /// <summary>True when ICS is handing out addresses on the hotspot (its DHCP server listens on the gateway address).</summary>
    public static bool IsServing(IPAddress gateway)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Any(e => e.Port == 67 && e.Address.Equals(gateway));
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    public static async Task<bool> WaitUntilServingAsync(IPAddress gateway, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsServing(gateway))
                return true;
            await Task.Delay(1000);
        }
        return IsServing(gateway);
    }

    /// <summary>ICS entries that point at adapters which are no longer installed.</summary>
    public static IReadOnlyList<string> FindStaleEntries()
    {
        var present = PresentAdapterIds();
        return Entries()
            .Where(e => e.Shared && !present.Contains(e.Guid))
            .Select(e => e.Guid)
            .ToList();
    }

    /// <summary>Clears every ICS public/private flag so Mobile Hotspot can configure sharing from scratch. Returns the cleared adapter ids.</summary>
    public static IReadOnlyList<string> ClearSharingEntries()
    {
        var cleared = new List<string>();
        using var searcher = new ManagementObjectSearcher(HomeNetScope, "SELECT * FROM HNet_ConnectionProperties");
        foreach (ManagementObject entry in searcher.Get())
        {
            using (entry)
            {
                if (entry["IsIcsPublic"] is not true && entry["IsIcsPrivate"] is not true)
                    continue;
                entry["IsIcsPublic"] = false;
                entry["IsIcsPrivate"] = false;
                entry.Put();
                cleared.Add(GuidOf(entry["Connection"] as string));
            }
        }
        return cleared;
    }

    public static void RestartSharingService()
    {
        using var service = new ServiceController("SharedAccess");
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    private static IEnumerable<(string Guid, bool Shared)> Entries()
    {
        using var searcher = new ManagementObjectSearcher(HomeNetScope, "SELECT Connection, IsIcsPublic, IsIcsPrivate FROM HNet_ConnectionProperties");
        foreach (ManagementObject entry in searcher.Get())
        {
            using (entry)
                yield return (GuidOf(entry["Connection"] as string), entry["IsIcsPublic"] is true || entry["IsIcsPrivate"] is true);
        }
    }

    private static HashSet<string> PresentAdapterIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2", "SELECT InterfaceGuid FROM MSFT_NetAdapter");
        foreach (ManagementObject adapter in searcher.Get())
        {
            using (adapter)
            {
                if (adapter["InterfaceGuid"] is string guid)
                    ids.Add(guid.Trim('{', '}'));
            }
        }
        return ids;
    }

    private static string GuidOf(string? connection) =>
        connection is null ? "" : GuidPattern().Match(connection).Groups[1].Value.ToUpperInvariant();

    [GeneratedRegex(@"\{([0-9A-Fa-f-]+)\}")]
    private static partial Regex GuidPattern();
}
