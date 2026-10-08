using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace AeroGatePilot.App.Localization;

/// <summary>Runtime-switchable string table. XAML binds through <see cref="TExtension"/>; code uses <see cref="T"/>.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _english = LoadTable("en");
    private readonly Dictionary<string, string> _persian = LoadTable("fa");
    private Dictionary<string, string> _current;

    private Loc() => _current = _english;

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string Language { get; private set; } = "en";
    public bool IsPersian => Language == "fa";
    public FlowDirection FlowDirection => IsPersian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string this[string key] =>
        _current.TryGetValue(key, out var value) ? value : _english.TryGetValue(key, out var fallback) ? fallback : key;

    public static string T(string key) => Instance[key];

    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public void SetLanguage(string language)
    {
        Language = language == "fa" ? "fa" : "en";
        _current = IsPersian ? _persian : _english;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPersian)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        LanguageChanged?.Invoke();
    }

    private static Dictionary<string, string> LoadTable(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Strings.{language}.json")
                           ?? throw new FileNotFoundException($"Missing string table for '{language}'.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
