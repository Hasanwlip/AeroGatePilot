using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AeroGatePilot.Core;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AeroGatePilot.Portal;

/// <summary>
/// Captive portal web server. Every HTTP request from an unauthenticated client lands here
/// (DNS answers point to the gateway); requests for foreign hosts are redirected to the login page.
/// </summary>
public sealed class PortalServer : IAsyncDisposable
{
    public static readonly string[] TemplateFiles = ["login.html", "status.html", "portal.css", "portal.js"];

    private static readonly Assembly ResourceAssembly = typeof(PortalServer).Assembly;
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
    };

    private readonly IPortalBackend _backend;
    private readonly bool _previewMode;
    private WebApplication? _app;
    private IPAddress? _servedAddress;

    public PortalServer(IPortalBackend backend, bool previewMode = false)
    {
        _backend = backend;
        _previewMode = previewMode;
    }

    /// <summary>Host (and optional port) clients use to reach the portal, e.g. "192.168.137.1".</summary>
    public string PublicHost { get; private set; } = "";

    public bool IsRunning => _app is not null;

    /// <param name="servedAddress">
    /// When binding to <see cref="IPAddress.Any"/>, only connections arriving on this address (or loopback) are answered.
    /// </param>
    public async Task StartAsync(IPAddress bindAddress, int port, string publicHost, IPAddress? servedAddress = null)
    {
        if (_app is not null)
            throw new InvalidOperationException("Portal is already running.");

        PublicHost = publicHost;
        _servedAddress = servedAddress;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = ResourceAssembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Listen(bindAddress, port);
        });

        var app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null)
            return;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Writes the built-in template files into <paramref name="directory"/> so they can be customized.</summary>
    public static void ExportDefaultTemplate(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in TemplateFiles)
            File.WriteAllText(Path.Combine(directory, file), ReadEmbeddedText(file), new UTF8Encoding(false));
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (_servedAddress is not null)
        {
            var local = ctx.Connection.LocalIpAddress?.MapToIPv4();
            if (local is null || (!local.Equals(_servedAddress) && !IPAddress.IsLoopback(local)))
            {
                ctx.Abort();
                return;
            }
        }

        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        var client = (ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback).MapToIPv4();
        var path = ctx.Request.Path.Value ?? "/";
        var settings = _backend.Settings;
        var lang = ResolveLanguage(ctx, settings.Branding);

        if (!IsPortalHost(ctx.Request.Host.Host))
        {
            await HandleForeignHostAsync(ctx, client, path);
            return;
        }

        if (path.StartsWith("/portal/assets/", StringComparison.OrdinalIgnoreCase))
        {
            await ServeAssetAsync(ctx, path["/portal/assets/".Length..], settings.Branding);
            return;
        }

        switch (path.TrimEnd('/').ToLowerInvariant())
        {
            case "/portal" when HttpMethods.IsGet(ctx.Request.Method):
                await ServePortalPageAsync(ctx, client, lang, error: null);
                return;
            case "/portal/login" when HttpMethods.IsPost(ctx.Request.Method):
                await HandleLoginAsync(ctx, client, lang, guest: false);
                return;
            case "/portal/guest" when HttpMethods.IsPost(ctx.Request.Method):
                await HandleLoginAsync(ctx, client, lang, guest: true);
                return;
            case "/portal/logout" when HttpMethods.IsPost(ctx.Request.Method):
                if (!_previewMode)
                    _backend.Logout(client);
                ctx.Response.Redirect(PortalUrl(lang, null));
                return;
            case "/portal/api/status":
                await WriteStatusJsonAsync(ctx, client, lang);
                return;
            default:
                ctx.Response.Redirect(PortalUrl(lang, null));
                return;
        }
    }

    private async Task HandleForeignHostAsync(HttpContext ctx, IPAddress client, string path)
    {
        var authorized = _backend.GetStatus(client) is not null;
        if (!authorized)
        {
            var original = $"http://{ctx.Request.Host}{ctx.Request.Path}{ctx.Request.QueryString}";
            ctx.Response.Redirect(PortalUrl(null, original));
            return;
        }

        // Authorized device that still has a stale DNS answer pointing at the gateway: satisfy OS connectivity probes.
        var lower = path.ToLowerInvariant();
        if (lower is "/generate_204" or "/gen_204")
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (lower.EndsWith("hotspot-detect.html") || ctx.Request.Host.Host.Contains("apple.com", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.Response.WriteAsync("<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success</BODY></HTML>");
            return;
        }
        if (lower == "/connecttest.txt")
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("Microsoft Connect Test");
            return;
        }
        if (lower == "/ncsi.txt")
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("Microsoft NCSI");
            return;
        }
        await ctx.Response.WriteAsync("<!doctype html><meta http-equiv=\"refresh\" content=\"2\"><body style=\"font-family:sans-serif;text-align:center;padding-top:20vh\">Connected. Reloading…</body>");
    }

    private async Task HandleLoginAsync(HttpContext ctx, IPAddress client, string lang, bool guest)
    {
        var form = await ctx.Request.ReadFormAsync();
        var dst = form["dst"].ToString();
        var settings = _backend.Settings;

        if (_previewMode)
        {
            await ServePortalPageAsync(ctx, client, lang, PortalText.For(lang)["error_Preview"], dst);
            return;
        }

        LoginResult result;
        if (guest)
        {
            result = settings.Portal.AuthMode == PortalAuthMode.UserPassword
                ? LoginResult.Fail(AccessDenyReason.GuestDisabled)
                : _backend.GuestLogin(client);
        }
        else
        {
            result = settings.Portal.AuthMode == PortalAuthMode.GuestClickThrough
                ? LoginResult.Fail(AccessDenyReason.InvalidCredentials)
                : _backend.Login(form["username"].ToString(), form["password"].ToString(), client);
        }

        if (result.Success)
        {
            ctx.Response.Redirect(PortalUrl(lang, dst));
            return;
        }
        await ServePortalPageAsync(ctx, client, lang, PortalText.Error(lang, result.Reason), dst, form["username"].ToString());
    }

    private async Task ServePortalPageAsync(HttpContext ctx, IPAddress client, string lang, string? error, string? dst = null, string username = "")
    {
        dst ??= ctx.Request.Query["dst"].ToString();
        var settings = _backend.Settings;
        var branding = settings.Branding;
        var status = _previewMode ? null : _backend.GetStatus(client);
        var (values, flags) = BaseModel(lang, branding);

        values["dst"] = dst ?? "";
        values["dst_query"] = Uri.EscapeDataString(dst ?? "");
        values["username_value"] = username;
        values["error"] = error ?? "";
        if (!string.IsNullOrEmpty(error))
            flags.Add("error");

        string template;
        if (status is null)
        {
            template = ReadTemplate("login.html");
            var mode = settings.Portal.AuthMode;
            if (mode is PortalAuthMode.UserPassword or PortalAuthMode.Both)
                flags.Add("user_form");
            if (mode is PortalAuthMode.GuestClickThrough or PortalAuthMode.Both)
                flags.Add("guest_form");
            if (mode == PortalAuthMode.Both)
                flags.Add("both_forms");
        }
        else
        {
            template = ReadTemplate("status.html");
            FillStatus(values, flags, status, lang);
            var continueUrl = !string.IsNullOrWhiteSpace(settings.Portal.SuccessRedirectUrl) ? settings.Portal.SuccessRedirectUrl : dst;
            if (!string.IsNullOrWhiteSpace(continueUrl) && Uri.TryCreate(continueUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                values["continue_url"] = uri.ToString();
                flags.Add("continue");
            }
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(TemplateRenderer.Render(template, values, flags));
    }

    private async Task WriteStatusJsonAsync(HttpContext ctx, IPAddress client, string lang)
    {
        var status = _previewMode ? null : _backend.GetStatus(client);
        ctx.Response.ContentType = "application/json";
        if (status is null)
        {
            await ctx.Response.WriteAsync("{\"connected\":false}");
            return;
        }
        var values = new Dictionary<string, string>();
        FillStatus(values, new HashSet<string>(), status, lang);
        values["connected"] = "true";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(values));
    }

    private static void FillStatus(Dictionary<string, string> values, HashSet<string> flags, PortalClientStatus status, string lang)
    {
        var text = PortalText.For(lang);
        values["display_name"] = status.DisplayName;
        values["plan"] = status.PlanName;
        values["data_used"] = Formatting.Bytes(status.UsedTotalBytes);
        values["session_data"] = Formatting.Bytes(status.SessionDownloadBytes + status.SessionUploadBytes);
        values["data_left"] = status.RemainingBytes is { } rb ? Formatting.Bytes(rb) : text["unlimited"];
        values["time_left"] = status.RemainingTime is { } rt ? Formatting.Duration(rt) : text["unlimited"];
        values["time_left_seconds"] = status.RemainingTime is { } rts ? ((long)rts.TotalSeconds).ToString(CultureInfo.InvariantCulture) : "";
        values["expires"] = status.ExpiresUtc is { } exp ? exp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : text["unlimited"];
        values["download_limit"] = status.DownloadKbps > 0 ? Formatting.Rate(status.DownloadKbps * 1000 / 8.0) : text["unlimited"];
        values["upload_limit"] = status.UploadKbps > 0 ? Formatting.Rate(status.UploadKbps * 1000 / 8.0) : text["unlimited"];
        var percent = status.DataLimitBytes is { } limit && limit > 0
            ? Math.Clamp(100.0 * status.UsedTotalBytes / limit, 0, 100)
            : 0;
        values["data_percent"] = percent.ToString("0.#", CultureInfo.InvariantCulture);
        if (status.DataLimitBytes is not null)
            flags.Add("data_limited");
        if (status.ExpiresUtc is not null)
            flags.Add("expires");
    }

    private (Dictionary<string, string> Values, HashSet<string> Flags) BaseModel(string lang, BrandingSettings branding)
    {
        var fa = lang == "fa";
        var values = new Dictionary<string, string>
        {
            ["lang"] = lang,
            ["dir"] = fa ? "rtl" : "ltr",
            ["title"] = fa && !string.IsNullOrWhiteSpace(branding.TitleFa) ? branding.TitleFa : branding.Title,
            ["subtitle"] = fa && !string.IsNullOrWhiteSpace(branding.SubtitleFa) ? branding.SubtitleFa : branding.Subtitle,
            ["terms"] = fa && !string.IsNullOrWhiteSpace(branding.TermsFa) ? branding.TermsFa : branding.Terms,
            ["footer"] = branding.Footer,
            ["primary"] = SafeColor(branding.PrimaryColor, "#6C5CE7"),
            ["accent"] = SafeColor(branding.AccentColor, "#00CEC9"),
            ["other_lang"] = fa ? "en" : "fa",
            ["other_lang_label"] = fa ? "English" : "فارسی",
            ["asset_version"] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in PortalText.For(lang))
            values["t." + key] = value;

        var flags = new HashSet<string>();
        if (HasBrandingFile(branding.LogoFile))
            flags.Add("logo");
        else
            flags.Add("no_logo");
        if (HasBrandingFile(branding.BackgroundFile))
            flags.Add("background");
        if (!string.IsNullOrWhiteSpace(values["terms"]))
            flags.Add("terms");
        if (branding.ShowLanguageSwitch)
            flags.Add("lang_switch");
        if (!string.IsNullOrWhiteSpace(branding.CustomCss))
            flags.Add("custom_css");
        if (!string.IsNullOrWhiteSpace(branding.Footer))
            flags.Add("footer");
        return (values, flags);
    }

    private async Task ServeAssetAsync(HttpContext ctx, string name, BrandingSettings branding)
    {
        ctx.Response.Headers.CacheControl = "public, max-age=300";
        switch (name.ToLowerInvariant())
        {
            case "custom.css":
                ctx.Response.ContentType = ContentTypes[".css"];
                await ctx.Response.WriteAsync(branding.CustomCss ?? "");
                return;
            case "logo":
                await ServeBrandingFileAsync(ctx, branding.LogoFile);
                return;
            case "background":
                await ServeBrandingFileAsync(ctx, branding.BackgroundFile);
                return;
        }

        if (name.Contains("..") || name.Contains('\\'))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var bytes = ReadTemplateBytes(name);
        if (bytes is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private async Task ServeBrandingFileAsync(HttpContext ctx, string fileName)
    {
        if (!HasBrandingFile(fileName))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var path = Path.Combine(_backend.BrandingDirectory, Path.GetFileName(fileName));
        ctx.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        await ctx.Response.SendFileAsync(path);
    }

    private bool HasBrandingFile(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && File.Exists(Path.Combine(_backend.BrandingDirectory, Path.GetFileName(fileName)));

    private string ReadTemplate(string name)
    {
        var custom = Path.Combine(_backend.TemplateDirectory, name);
        return File.Exists(custom) ? File.ReadAllText(custom) : ReadEmbeddedText(name);
    }

    private byte[]? ReadTemplateBytes(string name)
    {
        var custom = Path.Combine(_backend.TemplateDirectory, name);
        if (File.Exists(custom))
            return File.ReadAllBytes(custom);
        using var stream = ResourceAssembly.GetManifestResourceStream("Templates/" + name);
        if (stream is null)
            return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadEmbeddedText(string name)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream("Templates/" + name)
            ?? throw new FileNotFoundException("Missing embedded portal template.", name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private bool IsPortalHost(string host)
    {
        var publicHostName = PublicHost.Split(':')[0];
        return string.Equals(host, publicHostName, StringComparison.OrdinalIgnoreCase)
               || (_previewMode && (host is "localhost" or "127.0.0.1"));
    }

    private string PortalUrl(string? lang, string? dst)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(lang))
            query.Add("lang=" + Uri.EscapeDataString(lang));
        if (!string.IsNullOrEmpty(dst))
            query.Add("dst=" + Uri.EscapeDataString(dst));
        return $"http://{PublicHost}/portal/" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }

    private static string ResolveLanguage(HttpContext ctx, BrandingSettings branding)
    {
        var requested = ctx.Request.Query["lang"].ToString();
        if (requested is "en" or "fa")
        {
            ctx.Response.Cookies.Append("lang", requested, new CookieOptions { MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Lax });
            return requested;
        }
        var cookie = ctx.Request.Cookies["lang"];
        if (cookie is "en" or "fa")
            return cookie;
        return branding.DefaultLanguage == "fa" ? "fa" : "en";
    }

    private static string SafeColor(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        value = value.Trim();
        return value.Length is 4 or 7 or 9 && value[0] == '#' && value[1..].All(Uri.IsHexDigit) ? value : fallback;
    }
}
