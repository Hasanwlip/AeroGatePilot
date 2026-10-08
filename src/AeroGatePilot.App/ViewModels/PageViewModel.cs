using AeroGatePilot.App.Mvvm;

namespace AeroGatePilot.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(AppServices services) => Services = services;

    protected AppServices Services { get; }

    public abstract string Key { get; }

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>Called every second while the page is visible.</summary>
    public virtual void OnTick()
    {
    }

    /// <summary>Called when the UI language changes.</summary>
    public virtual void OnLanguageChanged() => RaiseAll();
}
