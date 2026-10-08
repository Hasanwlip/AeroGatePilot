using System.Collections.ObjectModel;
using System.Windows;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Infrastructure;

namespace AeroGatePilot.App.ViewModels;

public sealed record LogRow(string Time, LogLevel Level, string Message)
{
    public string Glyph => Level switch
    {
        LogLevel.Success => "\uE73E",
        LogLevel.Warning => "\uE7BA",
        LogLevel.Error => "\uE783",
        _ => "\uE946",
    };

    public string BrushKey => Level switch
    {
        LogLevel.Success => "SuccessBrush",
        LogLevel.Warning => "WarningBrush",
        LogLevel.Error => "DangerBrush",
        _ => "AccentBrush",
    };

    public object Brush => Application.Current.Resources[BrushKey];
}

public sealed record HistoryRow(SessionHistoryEntry Entry)
{
    public string Username => Entry.Username.StartsWith("guest:", StringComparison.Ordinal) ? "👤 " + Entry.Username[6..] : Entry.Username;
    public string Ip => Entry.Ip;
    public string Mac => Entry.Mac;
    public string Started => Entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Ended => Entry.EndedUtc.ToLocalTime().ToString("HH:mm");
    public string Download => Formatting.Bytes(Entry.DownloadBytes);
    public string Upload => Formatting.Bytes(Entry.UploadBytes);
    public string Reason => Entry.EndReason;
}

public sealed class ActivityViewModel : PageViewModel
{
    private const int MaxLogRows = 500;

    public ActivityViewModel(AppServices services)
        : base(services)
    {
        foreach (var entry in services.Log.Entries.Reverse())
            Log.Add(ToRow(entry));
        services.Log.Written += entry => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Insert(0, ToRow(entry));
            while (Log.Count > MaxLogRows)
                Log.RemoveAt(Log.Count - 1);
        });
    }

    public override string Key => "activity";

    public ObservableCollection<LogRow> Log { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    public override void OnActivated()
    {
        History.Clear();
        foreach (var entry in Services.Store.GetHistory())
            History.Add(new HistoryRow(entry));
    }

    private static LogRow ToRow(LogEntry entry) => new(entry.Time.ToString("HH:mm:ss"), entry.Level, entry.Message);
}
