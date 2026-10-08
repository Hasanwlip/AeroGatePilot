using System.Collections.ObjectModel;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Security;

namespace AeroGatePilot.App.ViewModels;

public sealed class UserRow
{
    public UserRow(UserAccount account, Plan? plan, bool online)
    {
        Account = account;
        PlanName = plan?.Name ?? "—";
        Online = online;

        var used = Formatting.Bytes(account.UsedTotalBytes);
        Used = plan is { DataLimitMb: > 0 } ? $"{used} / {Formatting.Bytes(plan.DataLimitMb * QuotaEvaluator.BytesPerMb)}" : used;
        Time = plan is { TimeLimitMinutes: > 0 }
            ? $"{Formatting.Duration(TimeSpan.FromSeconds(account.UsedSeconds))} / {Formatting.Duration(TimeSpan.FromMinutes(plan.TimeLimitMinutes))}"
            : Formatting.Duration(TimeSpan.FromSeconds(account.UsedSeconds));

        if (plan is null)
        {
            Expires = "—";
            Status = Loc.T("Msg_PlanRequired");
            StatusKind = "Danger";
            return;
        }

        var quota = QuotaEvaluator.Evaluate(account, plan, DateTime.UtcNow);
        Expires = quota.ExpiresUtc is { } exp
            ? exp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : plan.ValidityDays > 0 ? Loc.T("Status_NotStarted") : Loc.T("Unlimited");

        (Status, StatusKind) = quota.Reason switch
        {
            _ when online => (Loc.T("Status_Online"), "Success"),
            AccessDenyReason.AccountDisabled => (Loc.T("Status_Disabled"), "Muted"),
            AccessDenyReason.AccountExpired => (Loc.T("Status_Expired"), "Danger"),
            AccessDenyReason.DataExhausted or AccessDenyReason.TimeExhausted => (Loc.T("Status_Exhausted"), "Warning"),
            _ => (Loc.T("Status_Active"), "Accent"),
        };
    }

    public UserAccount Account { get; }
    public string Username => Account.IsGuest ? Account.Username.Replace("guest:", "👤 ") : Account.Username;
    public string DisplayName => Account.DisplayName;
    public string PlanName { get; }
    public bool Online { get; }
    public string Used { get; }
    public string Time { get; }
    public string Expires { get; }
    public string Status { get; }
    public string StatusKind { get; }
}

public sealed class UsersViewModel : PageViewModel
{
    private List<UserRow> _all = [];
    private string _search = "";
    private bool _showGuests;
    private UserRow? _selected;
    private UserAccount _editing = NewAccount(0);
    private string _password = "";
    private string _message = "";
    private int _tick;

    public UsersViewModel(AppServices services)
        : base(services)
    {
        NewCommand = new RelayCommand(StartNew);
        SaveCommand = new RelayCommand(Save);
        DeleteCommand = new RelayCommand(Delete, () => _editing.Id != 0);
        ResetUsageCommand = new RelayCommand(ResetUsage, () => _editing.Id != 0);
        KickCommand = new RelayCommand(Kick, () => _editing.Id != 0 && Services.Engine.Sessions.Sessions.Any(s => s.User.Id == _editing.Id));
        GeneratePasswordCommand = new RelayCommand(() => Password = PasswordHasher.GeneratePassword(8));
        DeleteGuestsCommand = new RelayCommand(DeleteGuests);
    }

    public override string Key => "users";

    public ObservableCollection<UserRow> Users { get; } = [];
    public ObservableCollection<Plan> Plans { get; } = [];

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ResetUsageCommand { get; }
    public RelayCommand KickCommand { get; }
    public RelayCommand GeneratePasswordCommand { get; }
    public RelayCommand DeleteGuestsCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
                ApplyFilter();
        }
    }

    public bool ShowGuests
    {
        get => _showGuests;
        set
        {
            if (Set(ref _showGuests, value))
                Reload();
        }
    }

    public UserRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null)
                return;
            Editing = value.Account.Clone();
            Password = "";
            Message = "";
        }
    }

    public UserAccount Editing
    {
        get => _editing;
        private set
        {
            Set(ref _editing, value);
            Raise(nameof(IsNew));
            Raise(nameof(EditorTitle));
            Raise(nameof(UsageSummary));
        }
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsNew => _editing.Id == 0;
    public string EditorTitle => IsNew ? Loc.T("Users_NewAccount") : _editing.Username;
    public string CountText => Loc.Format("Users_Count", Users.Count);

    public string UsageSummary =>
        IsNew ? "" : $"↓ {Formatting.Bytes(_editing.UsedDownloadBytes)}   ↑ {Formatting.Bytes(_editing.UsedUploadBytes)}   ⏱ {Formatting.Duration(TimeSpan.FromSeconds(_editing.UsedSeconds))}";

    public override void OnActivated()
    {
        Reload();
        if (_editing.Id == 0 && _editing.PlanId == 0)
            StartNew();
    }

    public override void OnTick()
    {
        if (++_tick % 5 == 0)
            Reload();
    }

    public override void OnLanguageChanged()
    {
        Reload();
        base.OnLanguageChanged();
    }

    private void Reload()
    {
        var plans = Services.Store.GetPlans();
        SyncPlans(plans);
        var online = Services.Engine.Sessions.Sessions.Select(s => s.User.Id).ToHashSet();
        _all = Services.Store.GetUsers(_showGuests)
            .Select(u => new UserRow(u, plans.FirstOrDefault(p => p.Id == u.PlanId), online.Contains(u.Id)))
            .ToList();
        ApplyFilter();
    }

    private void SyncPlans(IReadOnlyList<Plan> plans)
    {
        if (Plans.Select(p => (p.Id, p.Name)).SequenceEqual(plans.Select(p => (p.Id, p.Name))))
            return;
        var planId = _editing.PlanId;
        Plans.Clear();
        foreach (var plan in plans)
            Plans.Add(plan);
        _editing.PlanId = planId;
        Raise(nameof(Editing));
    }

    private void ApplyFilter()
    {
        var selectedId = _selected?.Account.Id;
        var term = _search.Trim();
        var rows = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(r => r.Account.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
                              || r.Account.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                              || r.PlanName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        Users.Clear();
        foreach (var row in rows)
            Users.Add(row);
        _selected = Users.FirstOrDefault(r => r.Account.Id == selectedId);
        Raise(nameof(Selected));
        Raise(nameof(CountText));
    }

    private void StartNew()
    {
        _selected = null;
        Raise(nameof(Selected));
        Editing = NewAccount(Plans.FirstOrDefault(p => p.DataLimitMb > 0 || p.ValidityDays > 0)?.Id ?? Plans.FirstOrDefault()?.Id ?? 0);
        Password = PasswordHasher.GeneratePassword(8);
        Message = "";
    }

    private void Save()
    {
        var store = Services.Store;
        var user = _editing;
        if (string.IsNullOrWhiteSpace(user.Username))
        {
            Message = Loc.T("Msg_UsernameRequired");
            return;
        }
        if (store.UsernameExists(user.Username, user.Id))
        {
            Message = Loc.T("Msg_UsernameTaken");
            return;
        }
        if (user.Id == 0 && string.IsNullOrEmpty(Password))
        {
            Message = Loc.T("Msg_PasswordRequired");
            return;
        }
        if (store.GetPlan(user.PlanId) is null)
        {
            Message = Loc.T("Msg_PlanRequired");
            return;
        }

        if (user.Id != 0 && store.GetUser(user.Id) is { } current)
        {
            user.UsedDownloadBytes = current.UsedDownloadBytes;
            user.UsedUploadBytes = current.UsedUploadBytes;
            user.UsedSeconds = current.UsedSeconds;
            user.FirstLoginUtc = current.FirstLoginUtc;
        }

        var saved = store.SaveUser(user, Password);
        if (!saved.Enabled)
            Services.Engine.Sessions.EndUserSessions(saved.Id, AccessDenyReason.AccountDisabled);
        Services.Log.Info($"Account \"{saved.Username}\" saved.");
        Message = Loc.T("Msg_Saved");
        Password = "";
        Reload();
        _selected = Users.FirstOrDefault(r => r.Account.Id == saved.Id);
        Raise(nameof(Selected));
        Editing = saved.Clone();
    }

    private void Delete()
    {
        if (!Dialogs.Confirm(Loc.Format("Msg_ConfirmDelete", _editing.Username)))
            return;
        Services.Engine.Sessions.EndUserSessions(_editing.Id, AccessDenyReason.AccountDisabled);
        Services.Store.DeleteUser(_editing.Id);
        Services.Log.Info($"Account \"{_editing.Username}\" deleted.");
        Reload();
        StartNew();
    }

    private void ResetUsage()
    {
        Services.Store.ResetUsage(_editing.Id);
        Services.Log.Info($"Usage of \"{_editing.Username}\" reset.");
        Reload();
        if (Services.Store.GetUser(_editing.Id) is { } fresh)
            Editing = fresh;
    }

    private void Kick() => Services.Engine.Sessions.EndUserSessions(_editing.Id, AccessDenyReason.KickedByAdmin);

    private void DeleteGuests()
    {
        foreach (var session in Services.Engine.Sessions.Sessions.Where(s => s.User.IsGuest).ToList())
            Services.Engine.KickDevice(session);
        Services.Store.DeleteGuests();
        Reload();
    }

    private static UserAccount NewAccount(long planId) => new() { PlanId = planId, Enabled = true };
}
