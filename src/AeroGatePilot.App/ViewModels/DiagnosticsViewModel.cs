using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Infrastructure.Network;

namespace AeroGatePilot.App.ViewModels;

public sealed record DiagnosticRow(string Title, DiagnosticStatus Status, string Detail);

public sealed class DiagnosticsViewModel : PageViewModel
{
    private IReadOnlyList<DiagnosticItem> _items = [];
    private bool _running;

    public DiagnosticsViewModel(AppServices services)
        : base(services)
    {
        RunCommand = new AsyncRelayCommand(RunAsync);
        OpenDataFolderCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.Paths.Root}\"") { UseShellExecute = true }));
    }

    public override string Key => "diagnostics";

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public ObservableCollection<DiagnosticRow> Items { get; } = [];

    public bool IsRunningChecks
    {
        get => _running;
        private set => Set(ref _running, value);
    }

    public string DataFolder => Services.Paths.Root;

    public string Version => "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    public override void OnActivated() => _ = RunAsync();

    public override void OnLanguageChanged()
    {
        Rebuild();
        base.OnLanguageChanged();
    }

    private async Task RunAsync()
    {
        if (IsRunningChecks)
            return;
        IsRunningChecks = true;
        try
        {
            var settings = Services.Settings.Current;
            _items = await Task.Run(() => NetworkDiagnostics.Run(settings));
            Rebuild();
        }
        finally
        {
            IsRunningChecks = false;
        }
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var item in _items)
            Items.Add(new DiagnosticRow(Loc.T(item.Key), item.Status, item.Detail));
    }
}
