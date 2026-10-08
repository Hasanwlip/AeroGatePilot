using System.Text.Json;
using System.Text.Json.Serialization;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Security;

namespace AeroGatePilot.Infrastructure;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly object _gate = new();

    public SettingsStore(string file)
    {
        _file = file;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _file, overwrite: true);
            Current = settings;
        }
        Changed?.Invoke(settings);
    }

    /// <summary>Deep copy, so editors can work on settings without touching the live instance.</summary>
    public AppSettings Snapshot() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Options), Options)!;

    private AppSettings Load()
    {
        AppSettings? settings = null;
        if (File.Exists(_file))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), Options);
            }
            catch (JsonException)
            {
                File.Copy(_file, _file + ".corrupt", overwrite: true);
            }
        }

        if (settings is null)
        {
            settings = new AppSettings();
            settings.Wifi.Passphrase = PasswordHasher.GeneratePassword(12);
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(_file, json);
        }
        return settings;
    }
}
