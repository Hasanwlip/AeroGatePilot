using System.Text.Json;

namespace AeroGatePilot.Infrastructure.Network;

public sealed record NetworkSnapshotData(DateTime TakenUtc, HotspotConfigSnapshot Hotspot);

/// <summary>
/// Persists the hotspot state found before AeroGate Pilot took over, so it can be restored on Stop,
/// on exit, or on the next launch after a crash.
/// </summary>
public sealed class NetworkSnapshot
{
    private readonly string _file;

    public NetworkSnapshot(string file) => _file = file;

    public bool Exists => File.Exists(_file);

    public void Save(NetworkSnapshotData data) =>
        File.WriteAllText(_file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

    public NetworkSnapshotData? Load()
    {
        try
        {
            return Exists ? JsonSerializer.Deserialize<NetworkSnapshotData>(File.ReadAllText(_file)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (Exists)
            File.Delete(_file);
    }

    public async Task<bool> RestoreAsync(AppLog log)
    {
        var data = Load();
        if (data is null)
        {
            Clear();
            return false;
        }

        try
        {
            await HotspotManager.RestoreAsync(data.Hotspot);
            log.Info($"Restored previous Mobile Hotspot settings (\"{data.Hotspot.Ssid}\").");
            Clear();
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Could not restore previous hotspot settings", ex);
            return false;
        }
    }
}
