using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using AeroGatePilot.Core.Models;
using Microsoft.Win32;
using Windows.Networking.NetworkOperators;

namespace AeroGatePilot.Infrastructure.Network;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Error,
    Info,
}

/// <summary>A single check. <see cref="Key"/> is a localization key; <see cref="Detail"/> is technical detail.</summary>
public sealed record DiagnosticItem(string Key, DiagnosticStatus Status, string Detail);

public sealed record AdapterInfo(string Name, string Description, string Kind, string Status, string Ipv4, string Speed, bool HasGateway);

public static class NetworkDiagnostics
{
    public const string DefaultGatewayAddress = "192.168.137.1";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The address Windows ICS gives the shared (hotspot) interface.</summary>
    public static IPAddress IcsGatewayAddress()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters");
            if (key?.GetValue("ScopeAddress") is string value && IPAddress.TryParse(value, out var address))
                return address;
        }
        catch (Exception)
        {
        }
        return IPAddress.Parse(DefaultGatewayAddress);
    }

    /// <summary>Finds the local interface that owns <paramref name="address"/> and returns its prefix length.</summary>
    /// <param name="requireUsable">Skip addresses that are still tentative (duplicate-address detection not finished).</param>
    public static (NetworkInterface Interface, int PrefixLength)? FindInterfaceWithAddress(IPAddress address, bool requireUsable = false)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !unicast.Address.Equals(address))
                    continue;
                if (requireUsable && unicast.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Tentative or DuplicateAddressDetectionState.Duplicate)
                    continue;
                return (nic, unicast.PrefixLength > 0 ? unicast.PrefixLength : 24);
            }
        }
        return null;
    }

    public static IReadOnlyList<AdapterInfo> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(n =>
            {
                var props = n.GetIPProperties();
                var ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var kind = n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    NetworkInterfaceType.Ppp => "PPP",
                    _ => n.NetworkInterfaceType.ToString(),
                };
                var speed = n.Speed > 0 ? (n.Speed >= 1_000_000_000 ? $"{n.Speed / 1_000_000_000.0:0.#} Gbps" : $"{n.Speed / 1_000_000} Mbps") : "";
                return new AdapterInfo(n.Name, n.Description, kind, n.OperationalStatus.ToString(), ipv4, speed, hasGateway);
            })
            .OrderBy(a => a.Status != "Up")
            .ThenBy(a => a.Kind)
            .ToList();
    }

    public static IReadOnlyList<DiagnosticItem> Run(AppSettings settings)
    {
        var items = new List<DiagnosticItem>();

        items.Add(IsAdministrator()
            ? new DiagnosticItem("Diag_Admin", DiagnosticStatus.Ok, "Running elevated")
            : new DiagnosticItem("Diag_Admin", DiagnosticStatus.Error, "Restart AeroGate Pilot with \"Run as administrator\""));

        items.Add(WinDivertNative.IsAvailable(out var divertError)
            ? new DiagnosticItem("Diag_WinDivert", DiagnosticStatus.Ok, "WinDivert 2.2.2")
            : new DiagnosticItem("Diag_WinDivert", DiagnosticStatus.Error, divertError));

        var wifiAdapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Add(wifiAdapters.Count > 0
            ? new DiagnosticItem("Diag_Wifi", DiagnosticStatus.Ok, string.Join(", ", wifiAdapters.Select(w => w.Description)))
            : new DiagnosticItem("Diag_Wifi", DiagnosticStatus.Error, "No Wi-Fi adapter found"));

        try
        {
            var candidates = HotspotManager.GetWanCandidates();
            var wan = candidates.FirstOrDefault(c => c.AdapterId.Equals(settings.Wifi.WanAdapterId, StringComparison.OrdinalIgnoreCase))
                      ?? candidates.FirstOrDefault(c => c.HasInternet);
            if (wan is null)
            {
                items.Add(new DiagnosticItem("Diag_Wan", DiagnosticStatus.Error, "No connection with internet access"));
            }
            else
            {
                var status = !wan.HasInternet ? DiagnosticStatus.Warning : wan.IsEthernet ? DiagnosticStatus.Ok : DiagnosticStatus.Warning;
                var detail = wan.IsEthernet ? $"{wan.Name} (Ethernet)" : $"{wan.Name} — Ethernet is recommended; sharing Wi-Fi over Wi-Fi depends on the adapter";
                items.Add(new DiagnosticItem("Diag_Wan", status, detail));
                items.Add(wan.Capability == TetheringCapability.Enabled
                    ? new DiagnosticItem("Diag_Tethering", DiagnosticStatus.Ok, "Windows Mobile Hotspot can share this connection")
                    : new DiagnosticItem("Diag_Tethering", DiagnosticStatus.Error, wan.Capability.ToString()));
            }
        }
        catch (Exception ex)
        {
            items.Add(new DiagnosticItem("Diag_Tethering", DiagnosticStatus.Error, ex.Message));
        }

        items.Add(settings.Wifi.OpenNetwork
            ? new DiagnosticItem("Diag_Open", DiagnosticStatus.Warning, "Windows Mobile Hotspot always uses WPA2; the network will be password protected. Publish the Wi-Fi password (e.g. on a sign or QR code) and keep accounts on the portal.")
            : new DiagnosticItem("Diag_Open", DiagnosticStatus.Info, "WPA2-Personal"));

        if (settings.Wifi.Passphrase.Length is < 8 or > 63)
            items.Add(new DiagnosticItem("Diag_Passphrase", DiagnosticStatus.Error, "Wi-Fi password must be 8–63 characters"));

        try
        {
            var stale = IcsRepair.FindStaleEntries();
            items.Add(stale.Count == 0
                ? new DiagnosticItem("Diag_Ics", DiagnosticStatus.Ok, "No leftover sharing settings")
                : new DiagnosticItem("Diag_Ics", DiagnosticStatus.Warning,
                    $"Sharing is still assigned to {stale.Count} removed adapter(s) (old VPN?), which stops phones from getting an IP address. AeroGate repairs this automatically on Start."));
        }
        catch (Exception ex)
        {
            items.Add(new DiagnosticItem("Diag_Ics", DiagnosticStatus.Info, $"Could not inspect sharing settings: {ex.Message}"));
        }

        items.Add(new DiagnosticItem("Diag_Gateway", DiagnosticStatus.Info, $"{IcsGatewayAddress()} (Windows ICS)"));

        var portalPort = settings.Portal.InternalPort;
        items.Add(IsPortFree(portalPort)
            ? new DiagnosticItem("Diag_Port", DiagnosticStatus.Ok, $"TCP {portalPort}")
            : new DiagnosticItem("Diag_Port", DiagnosticStatus.Warning, $"TCP {portalPort} is in use — choose another portal port"));

        return items;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.All(l => l.Port != port);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
