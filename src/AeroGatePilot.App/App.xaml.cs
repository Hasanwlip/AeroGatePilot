using System.Windows;
using System.Windows.Threading;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.ViewModels;
using AeroGatePilot.Infrastructure;
using AeroGatePilot.Infrastructure.Data;

namespace AeroGatePilot.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A separate data folder (for testing) is a separate instance.
        var dataDir = Environment.GetEnvironmentVariable("AEROGATE_DATA_DIR") is { Length: > 0 } dir ? System.IO.Path.GetFullPath(dir) : null;
        var instanceName = dataDir is null ? "SingleInstance" : $"Data.{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(dataDir):X8}";
        _singleInstance = new Mutex(true, $@"Global\AeroGatePilot.{instanceName}", out var created);
        if (!created)
        {
            MessageBox.Show(Loc.T("Msg_AlreadyRunning"), Loc.T("App_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = new AppPaths(dataDir);
        var log = new AppLog(paths.LogDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unexpected error", args.Exception);
            MessageBox.Show(args.Exception.Message, Loc.T("Msg_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("Background task failed", args.Exception.GetBaseException());
            args.SetObserved();
        };

        try
        {
            var settings = new SettingsStore(paths.SettingsFile);
            var store = new SqliteStore(paths.DatabaseFile);
            EnsureGuestPlan(settings, store);
            Loc.Instance.SetLanguage(settings.Current.Language);

            var engine = new GatewayEngine(paths, settings, store, log);
            _services = new AppServices(paths, settings, store, log, engine);

            var window = new MainWindow(new MainViewModel(_services));
            MainWindow = window;
            window.Show();

            log.Info("AeroGate Pilot started.");
            await engine.RecoverAsync();
        }
        catch (Exception ex)
        {
            log.Error("Startup failed", ex);
            MessageBox.Show(ex.Message, Loc.T("Msg_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void EnsureGuestPlan(SettingsStore settings, SqliteStore store)
    {
        if (store.GetPlan(settings.Current.Portal.GuestPlanId) is not null)
            return;
        var plans = store.GetPlans();
        var guest = plans.FirstOrDefault(p => p.Name.StartsWith("Guest", StringComparison.OrdinalIgnoreCase)) ?? plans.FirstOrDefault();
        if (guest is null)
            return;
        var snapshot = settings.Snapshot();
        snapshot.Portal.GuestPlanId = guest.Id;
        settings.Save(snapshot);
    }
}
