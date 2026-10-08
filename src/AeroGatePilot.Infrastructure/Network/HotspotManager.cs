using AeroGatePilot.Core.Models;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace AeroGatePilot.Infrastructure.Network;

public sealed record WanCandidate(
    string AdapterId,
    string Name,
    bool IsEthernet,
    bool IsWifi,
    bool HasInternet,
    TetheringCapability Capability)
{
    public bool CanShare => Capability == TetheringCapability.Enabled;
    public override string ToString() => Name;
}

public sealed record HotspotClient(string MacAddress, IReadOnlyList<string> HostNames);

public sealed record HotspotConfigSnapshot(string Ssid, string Passphrase, string Band, bool WasRunning, string AdapterId);

/// <summary>Controls Windows Mobile Hotspot (Wi-Fi tethering + ICS NAT/DHCP/DNS) through the WinRT tethering API.</summary>
public sealed class HotspotManager
{
    private NetworkOperatorTetheringManager? _manager;

    public static IReadOnlyList<WanCandidate> GetWanCandidates()
    {
        var result = new List<WanCandidate>();
        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            var adapter = profile.NetworkAdapter;
            if (adapter is null)
                continue;
            var level = profile.GetNetworkConnectivityLevel();
            if (level == NetworkConnectivityLevel.None)
                continue;

            TetheringCapability capability;
            try
            {
                capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            }
            catch (Exception)
            {
                capability = TetheringCapability.DisabledBySystemCapability;
            }

            var id = adapter.NetworkAdapterId.ToString();
            if (result.Any(r => r.AdapterId == id))
                continue;
            result.Add(new WanCandidate(
                id,
                profile.ProfileName,
                adapter.IanaInterfaceType == 6,
                adapter.IanaInterfaceType == 71,
                level == NetworkConnectivityLevel.InternetAccess,
                capability));
        }

        return result
            .OrderByDescending(c => c.IsEthernet)
            .ThenByDescending(c => c.HasInternet)
            .ToList();
    }

    public static ConnectionProfile? FindProfile(string adapterId)
    {
        var profiles = NetworkInformation.GetConnectionProfiles().Where(p => p.NetworkAdapter is not null).ToList();
        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            var match = profiles.FirstOrDefault(p => p.NetworkAdapter.NetworkAdapterId.ToString().Equals(adapterId, StringComparison.OrdinalIgnoreCase)
                                                     && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
            if (match is not null)
                return match;
        }

        return profiles
                   .Where(p => p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
                   .OrderByDescending(p => p.NetworkAdapter.IanaInterfaceType == 6)
                   .FirstOrDefault()
               ?? NetworkInformation.GetInternetConnectionProfile();
    }

    public bool IsRunning => _manager?.TetheringOperationalState == TetheringOperationalState.On;

    public int ClientCount => _manager is null ? 0 : (int)_manager.ClientCount;

    public IReadOnlyList<HotspotClient> GetClients()
    {
        if (_manager is null)
            return [];
        try
        {
            return _manager.GetTetheringClients()
                .Select(c => new HotspotClient(MacResolver.Normalize(c.MacAddress), c.HostNames.Select(h => h.CanonicalName).ToList()))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public HotspotConfigSnapshot Capture(ConnectionProfile profile)
    {
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        var config = manager.GetCurrentAccessPointConfiguration();
        return new HotspotConfigSnapshot(
            config.Ssid,
            config.Passphrase,
            config.Band.ToString(),
            manager.TetheringOperationalState == TetheringOperationalState.On,
            profile.NetworkAdapter.NetworkAdapterId.ToString());
    }

    public async Task StartAsync(ConnectionProfile profile, WifiSettings wifi)
    {
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

        if (manager.TetheringOperationalState == TetheringOperationalState.On)
        {
            var stop = await manager.StopTetheringAsync();
            if (stop.Status != TetheringOperationStatus.Success)
                throw new InvalidOperationException($"Could not restart the existing hotspot ({stop.Status}). {stop.AdditionalErrorMessage}");
        }

        var config = manager.GetCurrentAccessPointConfiguration();
        config.Ssid = wifi.Ssid;
        config.Passphrase = wifi.Passphrase;
        var band = wifi.Band switch
        {
            WifiBand.TwoPointFourGHz => TetheringWiFiBand.TwoPointFourGigahertz,
            WifiBand.FiveGHz => TetheringWiFiBand.FiveGigahertz,
            _ => TetheringWiFiBand.Auto,
        };
        if (band == TetheringWiFiBand.Auto || config.IsBandSupported(band))
            config.Band = band;
        await manager.ConfigureAccessPointAsync(config);

        var result = await manager.StartTetheringAsync();
        if (result.Status != TetheringOperationStatus.Success)
            throw new InvalidOperationException(DescribeFailure(result));
        _manager = manager;
    }

    public async Task StopAsync()
    {
        var manager = _manager;
        _manager = null;
        if (manager is not null && manager.TetheringOperationalState == TetheringOperationalState.On)
            await manager.StopTetheringAsync();
    }

    /// <summary>Puts the hotspot configuration back to what it was before AeroGate Pilot changed it.</summary>
    public static async Task RestoreAsync(HotspotConfigSnapshot snapshot)
    {
        var profile = FindProfile(snapshot.AdapterId);
        if (profile is null)
            return;
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        if (manager.TetheringOperationalState == TetheringOperationalState.On)
            await manager.StopTetheringAsync();

        var config = manager.GetCurrentAccessPointConfiguration();
        if (!string.IsNullOrEmpty(snapshot.Ssid))
            config.Ssid = snapshot.Ssid;
        if (snapshot.Passphrase.Length >= 8)
            config.Passphrase = snapshot.Passphrase;
        if (Enum.TryParse<TetheringWiFiBand>(snapshot.Band, out var band) && (band == TetheringWiFiBand.Auto || config.IsBandSupported(band)))
            config.Band = band;
        await manager.ConfigureAccessPointAsync(config);

        if (snapshot.WasRunning)
            await manager.StartTetheringAsync();
    }

    private static string DescribeFailure(NetworkOperatorTetheringOperationResult result)
    {
        var hint = result.Status switch
        {
            TetheringOperationStatus.WiFiDeviceOff => "Wi-Fi is turned off. Turn Wi-Fi on and try again.",
            TetheringOperationStatus.MobileBroadbandDeviceOff => "The shared connection is turned off.",
            TetheringOperationStatus.EntitlementCheckFailure => "The provider does not allow sharing this connection.",
            TetheringOperationStatus.OperationInProgress => "Windows is already changing the hotspot. Wait a moment and retry.",
            TetheringOperationStatus.BluetoothDeviceOff => "Bluetooth is off.",
            TetheringOperationStatus.NetworkLimitedConnectivity => "The selected internet connection has limited connectivity.",
            _ => "Windows refused to start the Mobile Hotspot.",
        };
        return $"{hint} ({result.Status}) {result.AdditionalErrorMessage}".Trim();
    }
}
