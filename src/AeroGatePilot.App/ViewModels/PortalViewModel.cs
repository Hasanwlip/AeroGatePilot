using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using AeroGatePilot.App.Localization;
using AeroGatePilot.App.Mvvm;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Portal;
using Microsoft.Win32;

namespace AeroGatePilot.App.ViewModels;

public sealed partial class PortalViewModel : PageViewModel
{
    private PortalSettings _portal = new();
    private BrandingSettings _branding = new();
    private string _message = "";
    private bool _messageIsError;
    private PortalServer? _preview;
    private int _previewPort;

    public PortalViewModel(AppServices services)
        : base(services)
    {
        SaveCommand = new RelayCommand(() => Save(showMessage: true));
        PickLogoCommand = new RelayCommand(() => PickImage(isLogo: true));
        ClearLogoCommand = new RelayCommand(() => ClearImage(isLogo: true));
        PickBackgroundCommand = new RelayCommand(() => PickImage(isLogo: false));
        ClearBackgroundCommand = new RelayCommand(() => ClearImage(isLogo: false));
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        ExportTemplateCommand = new RelayCommand(ExportTemplate);
        ResetTemplateCommand = new RelayCommand(ResetTemplate);
        OpenTemplateFolderCommand = new RelayCommand(() => OpenFolder(Services.Paths.TemplateDirectory));
        Load();
    }

    public override string Key => "portal";

    public RelayCommand SaveCommand { get; }
    public RelayCommand PickLogoCommand { get; }
    public RelayCommand ClearLogoCommand { get; }
    public RelayCommand PickBackgroundCommand { get; }
    public RelayCommand ClearBackgroundCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public RelayCommand ExportTemplateCommand { get; }
    public RelayCommand ResetTemplateCommand { get; }
    public RelayCommand OpenTemplateFolderCommand { get; }

    public ObservableCollection<Plan> Plans { get; } = [];

    public IReadOnlyList<Option<string>> Languages =>
    [
        new("en", Loc.T("Lang_English")),
        new("fa", Loc.T("Lang_Persian")),
    ];

    public PortalSettings PortalSettings
    {
        get => _portal;
        private set => Set(ref _portal, value);
    }

    public BrandingSettings Branding
    {
        get => _branding;
        private set => Set(ref _branding, value);
    }

    public PortalAuthMode AuthMode
    {
        get => _portal.AuthMode;
        set
        {
            _portal.AuthMode = value;
            Raise();
            Raise(nameof(UsesGuest));
        }
    }

    public bool UsesGuest => _portal.AuthMode != PortalAuthMode.UserPassword;

    public string PrimaryColor
    {
        get => _branding.PrimaryColor;
        set
        {
            _branding.PrimaryColor = value;
            Raise();
        }
    }

    public string AccentColor
    {
        get => _branding.AccentColor;
        set
        {
            _branding.AccentColor = value;
            Raise();
        }
    }

    public BitmapImage? LogoPreview => LoadImage(_branding.LogoFile);
    public BitmapImage? BackgroundPreview => LoadImage(_branding.BackgroundFile);
    public bool HasLogo => LogoPreview is not null;
    public bool HasBackground => BackgroundPreview is not null;

    public string TemplateStatus => Loc.T(PortalServer.TemplateFiles.Any(f => File.Exists(Path.Combine(Services.Paths.TemplateDirectory, f)))
        ? "Portal_TemplateCustom"
        : "Portal_TemplateBuiltin");

    public string TemplateDirectory => Services.Paths.TemplateDirectory;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public override void OnActivated() => Load();

    private void Load()
    {
        var settings = Services.Settings.Snapshot();
        PortalSettings = settings.Portal;
        Branding = settings.Branding;
        Plans.Clear();
        foreach (var plan in Services.Store.GetPlans())
            Plans.Add(plan);
        Message = "";
        RaiseAll();
    }

    private bool Save(bool showMessage)
    {
        if (!IsColor(_branding.PrimaryColor) || !IsColor(_branding.AccentColor))
        {
            MessageIsError = true;
            Message = Loc.T("Msg_InvalidColor");
            return false;
        }

        var settings = Services.Settings.Snapshot();
        settings.Portal.AuthMode = _portal.AuthMode;
        settings.Portal.GuestPlanId = _portal.GuestPlanId;
        settings.Portal.SuccessRedirectUrl = _portal.SuccessRedirectUrl?.Trim() ?? "";
        settings.Branding = _branding;
        Services.Settings.Save(settings);
        Branding = Services.Settings.Snapshot().Branding;
        if (showMessage)
        {
            Services.Log.Info("Portal settings saved.");
            MessageIsError = false;
            Message = Loc.T("Msg_Saved");
        }
        RaiseAll();
        return true;
    }

    private void PickImage(bool isLogo)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.svg;*.webp|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        var baseName = isLogo ? "logo" : "background";
        foreach (var old in Directory.GetFiles(Services.Paths.BrandingDirectory, baseName + ".*"))
            File.Delete(old);
        var fileName = baseName + Path.GetExtension(dialog.FileName).ToLowerInvariant();
        File.Copy(dialog.FileName, Path.Combine(Services.Paths.BrandingDirectory, fileName), overwrite: true);

        if (isLogo)
            _branding.LogoFile = fileName;
        else
            _branding.BackgroundFile = fileName;
        Save(showMessage: true);
    }

    private void ClearImage(bool isLogo)
    {
        var fileName = isLogo ? _branding.LogoFile : _branding.BackgroundFile;
        if (!string.IsNullOrEmpty(fileName))
        {
            var path = Path.Combine(Services.Paths.BrandingDirectory, Path.GetFileName(fileName));
            if (File.Exists(path))
                File.Delete(path);
        }
        if (isLogo)
            _branding.LogoFile = "";
        else
            _branding.BackgroundFile = "";
        Save(showMessage: true);
    }

    private async Task PreviewAsync()
    {
        if (!Save(showMessage: false))
            return;

        if (_preview is null)
        {
            _previewPort = FreePort();
            _preview = new PortalServer(Services.Engine, previewMode: true);
            await _preview.StartAsync(IPAddress.Loopback, _previewPort, $"127.0.0.1:{_previewPort}");
        }
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_previewPort}/portal/?lang={Services.Settings.Current.Branding.DefaultLanguage}") { UseShellExecute = true });
    }

    private void ExportTemplate()
    {
        PortalServer.ExportDefaultTemplate(Services.Paths.TemplateDirectory);
        Raise(nameof(TemplateStatus));
        OpenFolder(Services.Paths.TemplateDirectory);
        MessageIsError = false;
        Message = Loc.T("Msg_TemplateExported");
    }

    private void ResetTemplate()
    {
        foreach (var file in PortalServer.TemplateFiles)
        {
            var path = Path.Combine(Services.Paths.TemplateDirectory, file);
            if (File.Exists(path))
                File.Delete(path);
        }
        Raise(nameof(TemplateStatus));
    }

    private BitmapImage? LoadImage(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var path = Path.Combine(Services.Paths.BrandingDirectory, Path.GetFileName(fileName));
        if (!File.Exists(path) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsColor(string value) => ColorPattern().IsMatch(value ?? "");

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex ColorPattern();
}
