using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Infrastructure;

namespace AeroGatePilot.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private PageViewModel _currentPage;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Dashboard = new DashboardViewModel(services);
        Pages =
        [
            Dashboard,
            new UsersViewModel(services),
            new PlansViewModel(services),
            new NetworkViewModel(services),
            new PortalViewModel(services),
            new DiagnosticsViewModel(services),
            new ActivityViewModel(services),
        ];
        _currentPage = Dashboard;

        ToggleGatewayCommand = new AsyncRelayCommand(ToggleGatewayAsync, () => Engine.State is not (GatewayState.Starting or GatewayState.Stopping));
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);

        Engine.StateChanged += () => Application.Current.Dispatcher.BeginInvoke(OnEngineStateChanged);
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public DashboardViewModel Dashboard { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (Set(ref _currentPage, value))
                Raise(nameof(CurrentPageKey));
        }
    }

    public string CurrentPageKey
    {
        get => _currentPage.Key;
        set
        {
            if (value != _currentPage.Key)
                Navigate(value);
        }
    }

    public AsyncRelayCommand ToggleGatewayCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }

    public GatewayEngine Engine => _services.Engine;
    public bool IsRunning => Engine.State == GatewayState.Running;
    public bool IsBusy => Engine.State is GatewayState.Starting or GatewayState.Stopping;
    public string StateText => Loc.T("State_" + Engine.State);
    public string ToggleText => Loc.T(IsRunning || Engine.State == GatewayState.Starting ? "Btn_Stop" : "Btn_Start");

    public Brush StateBrush => (Brush)Application.Current.Resources[Engine.State switch
    {
        GatewayState.Running => "SuccessBrush",
        GatewayState.Faulted => "DangerBrush",
        GatewayState.Starting or GatewayState.Stopping => "WarningBrush",
        _ => "MutedBrush",
    }];

    public void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key) ?? Dashboard;
        CurrentPage = page;
        page.OnActivated();
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await Engine.StopAsync();
    }

    private async Task ToggleGatewayAsync()
    {
        if (Engine.State is GatewayState.Running)
        {
            await Engine.StopAsync();
            return;
        }

        if (!await Engine.StartAsync() && !string.IsNullOrWhiteSpace(Engine.LastError))
            Dialogs.Error(Engine.LastError);
    }

    private void ToggleLanguage()
    {
        var language = Loc.Instance.IsPersian ? "en" : "fa";
        var settings = _services.Settings.Snapshot();
        settings.Language = language;
        _services.Settings.Save(settings);
        Loc.Instance.SetLanguage(language);
    }

    private void OnLanguageChanged()
    {
        RaiseAll();
        foreach (var page in Pages)
            page.OnLanguageChanged();
    }

    private void OnEngineStateChanged()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsBusy));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(ToggleText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        Dashboard.OnTick();
    }

    private void Tick()
    {
        Dashboard.Sample();
        CurrentPage.OnTick();
    }
}
