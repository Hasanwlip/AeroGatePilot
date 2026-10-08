using System.Collections.ObjectModel;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.App.ViewModels;

public sealed class PlanRow(Plan plan)
{
    public Plan Plan { get; } = plan;
    public string Name => Plan.Name;
    public string Data => Plan.DataLimitMb > 0 ? Formatting.Bytes(Plan.DataLimitMb * 1024 * 1024) : Loc.T("Unlimited");

    public string Time
    {
        get
        {
            var parts = new List<string>();
            if (Plan.TimeLimitMinutes > 0)
                parts.Add(Formatting.Minutes(Plan.TimeLimitMinutes));
            if (Plan.SessionLimitMinutes > 0)
                parts.Add($"{Formatting.Minutes(Plan.SessionLimitMinutes)} / {Loc.T("Col_Session")}");
            return parts.Count > 0 ? string.Join("  ·  ", parts) : Loc.T("Unlimited");
        }
    }

    public string Validity => Plan.ValidityDays > 0 ? $"{Plan.ValidityDays} d" : Loc.T("Unlimited");

    public string Speed =>
        Plan.DownloadKbps <= 0 && Plan.UploadKbps <= 0
            ? Loc.T("Unlimited")
            : $"↓ {Rate(Plan.DownloadKbps)}   ↑ {Rate(Plan.UploadKbps)}";

    public string Devices => Plan.MaxDevices > 0 ? Plan.MaxDevices.ToString() : Loc.T("Unlimited");

    public string Access
    {
        get
        {
            var count = DomainRules.Parse(Plan.SiteList).Count;
            var sites = Plan.SiteFilter switch
            {
                SiteFilterMode.BlockListed => Loc.Format("Sites_BlockCount", count),
                SiteFilterMode.AllowListed => Loc.Format("Sites_OnlyCount", count),
                _ => Loc.T("Sites_Off"),
            };
            return Plan.UseUpstreamProxy ? $"{sites}  ·  VPN" : sites;
        }
    }

    private static string Rate(int kbps) => kbps > 0 ? Formatting.Rate(kbps * 1000 / 8.0) : "∞";
}

public sealed class PlansViewModel : PageViewModel
{
    private PlanRow? _selected;
    private Plan _editing = new() { MaxDevices = 1 };
    private string _message = "";

    public PlansViewModel(AppServices services)
        : base(services)
    {
        NewCommand = new RelayCommand(StartNew);
        SaveCommand = new RelayCommand(Save);
        DeleteCommand = new RelayCommand(Delete, () => _editing.Id != 0);
    }

    public override string Key => "plans";

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public PlanRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null)
                return;
            Editing = value.Plan.Clone();
            Message = "";
        }
    }

    public Plan Editing
    {
        get => _editing;
        private set
        {
            Set(ref _editing, value);
            Raise(nameof(EditorTitle));
            Raise(nameof(SiteFilter));
            Raise(nameof(SiteList));
            Raise(nameof(UseVpn));
            RaiseSiteState();
        }
    }

    public string EditorTitle => _editing.Id == 0 ? Loc.T("Plans_New") : _editing.Name;

    public SiteFilterMode SiteFilter
    {
        get => _editing.SiteFilter;
        set
        {
            _editing.SiteFilter = value;
            Raise();
            RaiseSiteState();
        }
    }

    public string SiteList
    {
        get => _editing.SiteList;
        set
        {
            _editing.SiteList = value;
            Raise();
            RaiseSiteState();
        }
    }

    public bool UseVpn
    {
        get => _editing.UseUpstreamProxy;
        set
        {
            _editing.UseUpstreamProxy = value;
            Raise();
        }
    }

    public bool SiteListVisible => _editing.SiteFilter != SiteFilterMode.Off;

    public string SiteModeHint => _editing.SiteFilter == SiteFilterMode.AllowListed ? Loc.T("Sites_AllowHint") : Loc.T("Sites_BlockHint");

    public string SiteListWarning
    {
        get
        {
            var invalid = DomainRules.InvalidEntries(_editing.SiteList);
            return invalid.Count == 0 ? "" : Loc.Format("Sites_Invalid", string.Join(", ", invalid.Take(5)));
        }
    }

    private void RaiseSiteState()
    {
        Raise(nameof(SiteListVisible));
        Raise(nameof(SiteModeHint));
        Raise(nameof(SiteListWarning));
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public override void OnActivated() => Reload();

    public override void OnLanguageChanged()
    {
        Reload();
        base.OnLanguageChanged();
    }

    private void Reload()
    {
        var selectedId = _selected?.Plan.Id;
        Plans.Clear();
        foreach (var plan in Services.Store.GetPlans())
            Plans.Add(new PlanRow(plan));
        _selected = Plans.FirstOrDefault(p => p.Plan.Id == selectedId);
        Raise(nameof(Selected));
    }

    private void StartNew()
    {
        _selected = null;
        Raise(nameof(Selected));
        Editing = new Plan { Name = "", MaxDevices = 1 };
        Message = "";
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_editing.Name))
        {
            Message = Loc.T("Msg_PlanNameRequired");
            return;
        }
        if (_editing.SiteFilter == SiteFilterMode.AllowListed && DomainRules.Parse(_editing.SiteList).IsEmpty)
        {
            Message = Loc.T("Msg_AllowListEmpty");
            return;
        }
        var saved = Services.Store.SavePlan(_editing);
        Services.Log.Info($"Plan \"{saved.Name}\" saved.");
        Message = Loc.T("Msg_Saved");
        Reload();
        _selected = Plans.FirstOrDefault(p => p.Plan.Id == saved.Id);
        Raise(nameof(Selected));
        Editing = saved.Clone();
    }

    private void Delete()
    {
        if (!Dialogs.Confirm(Loc.Format("Msg_ConfirmDelete", _editing.Name)))
            return;
        if (!Services.Store.DeletePlan(_editing.Id))
        {
            Message = Loc.T("Msg_PlanInUse");
            return;
        }
        Services.Log.Info($"Plan \"{_editing.Name}\" deleted.");
        Reload();
        StartNew();
    }
}
