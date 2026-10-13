using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Net;
using AeroGatePilot.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AeroGatePilot.Infrastructure.Api;

/// <summary>Token-protected Admin API for external panels (3x-ui style integration).</summary>
public sealed class AdminApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GatewayEngine _engine;
    private readonly SqliteStore _store;
    private readonly AppLog _log;
    private WebApplication? _app;

    public AdminApiServer(GatewayEngine engine, SqliteStore store, AppLog log)
    {
        _engine = engine;
        _store = store;
        _log = log;
    }

    public bool IsRunning => _app is not null;
    public string BaseUrl { get; private set; } = "";

    public async Task StartAsync(AdminApiSettings settings)
    {
        if (_app is not null)
            await StopAsync();

        if (!settings.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(settings.Token) || settings.Token.Length < 16)
            throw new InvalidOperationException("Admin API token must be at least 16 characters. Generate one on the Network page.");
        if (settings.Port is < 1024 or > 65535 || settings.Port == 80)
            throw new InvalidOperationException("Admin API port must be between 1024 and 65535.");
        if (!IPAddress.TryParse(settings.ListenAddress.Trim(), out var bind))
            throw new InvalidOperationException("Admin API listen address is invalid (use 127.0.0.1 or 0.0.0.0).");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 256 * 1024;
            options.Listen(bind, settings.Port);
        });

        var app = builder.Build();
        var token = settings.Token.Trim();
        app.Use(async (ctx, next) =>
        {
            if (!IsAuthorized(ctx, token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await WriteJson(ctx, new { error = "unauthorized" });
                return;
            }
            await next();
        });

        app.MapGet("/api/v1/status", () => Results.Json(StatusDto(), JsonOptions));
        app.MapGet("/api/v1/users", () => Results.Json(_store.GetUsers(includeGuests: false).Select(UserDto), JsonOptions));
        app.MapGet("/api/v1/users/{username}", (string username) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            return Results.Json(UserDetailDto(user), JsonOptions);
        });
        app.MapGet("/api/v1/sessions", () => Results.Json(_engine.Sessions.Sessions.Select(SessionDto), JsonOptions));
        app.MapGet("/api/v1/clients", () => Results.Json(_engine.ListObservedClients(), JsonOptions));
        app.MapGet("/api/v1/usage", (HttpRequest request) =>
        {
            DateTime? since = null;
            if (DateTime.TryParse(request.Query["since"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                since = parsed.ToUniversalTime();
            return Results.Json(new
            {
                since,
                users = _store.GetUsageSince(since).Select(UserDto),
            }, JsonOptions);
        });
        app.MapGet("/api/v1/users/{username}/history", (string username) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            return Results.Json(_store.GetHistoryForUser(username).Select(HistoryDto), JsonOptions);
        });
        app.MapPost("/api/v1/users/{username}/pause", async (string username, HttpRequest request) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            PauseBody? body = null;
            if (request.ContentLength is > 0)
                body = await JsonSerializer.DeserializeAsync<PauseBody>(request.Body, JsonOptions);
            var until = body?.Until ?? DateTime.Today.AddDays(1).ToUniversalTime();
            if (until.Kind == DateTimeKind.Unspecified)
                until = DateTime.SpecifyKind(until, DateTimeKind.Local).ToUniversalTime();
            _store.SetPausedUntil(user.Id, until);
            var closed = _engine.KickUser(username);
            return Results.Json(new { ok = true, username = user.Username, paused_until_utc = until, sessions_closed = closed }, JsonOptions);
        });
        app.MapPost("/api/v1/users/{username}/resume", (string username) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            _store.SetPausedUntil(user.Id, null);
            return Results.Json(new { ok = true, username = user.Username }, JsonOptions);
        });
        app.MapPost("/api/v1/users/{username}/kick", (string username) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            var kicked = _engine.KickUser(username);
            return Results.Json(new { ok = true, username = user.Username, sessions_closed = kicked }, JsonOptions);
        });
        app.MapGet("/api/v1/plans", () => Results.Json(_store.GetPlans().Select(PlanDto), JsonOptions));
        app.MapPost("/api/v1/plans", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<PlanBody>(request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.Json(new { error = "name_required" }, JsonOptions, statusCode: 400);
            var saved = _store.SavePlan(ApplyPlan(new Plan(), body));
            return Results.Json(PlanDto(saved), JsonOptions, statusCode: 201);
        });
        app.MapMethods("/api/v1/plans/{id:long}", ["PATCH"], async (long id, HttpRequest request) =>
        {
            var plan = _store.GetPlan(id);
            if (plan is null)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            var body = await JsonSerializer.DeserializeAsync<PlanBody>(request.Body, JsonOptions) ?? new();
            return Results.Json(PlanDto(_store.SavePlan(ApplyPlan(plan, body))), JsonOptions);
        });
        app.MapDelete("/api/v1/plans/{id:long}", (long id) =>
        {
            if (_store.GetPlan(id) is null)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            if (!_store.DeletePlan(id))
                return Results.Json(new { error = "plan_in_use" }, JsonOptions, statusCode: 409);
            return Results.Json(new { ok = true }, JsonOptions);
        });
        app.MapPost("/api/v1/auth/verify", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<AuthVerifyBody>(request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Username) || body.Password is null)
                return Results.Json(new { ok = false, reason = "invalid_request" }, JsonOptions, statusCode: 400);

            var (ok, reason, user, plan, quota) = _engine.Sessions.VerifyCredentials(body.Username, body.Password);
            if (!ok || user is null)
                return Results.Json(new { ok = false, reason = reason.ToString() }, JsonOptions);

            return Results.Json(new
            {
                ok = true,
                username = user.Username,
                display_name = user.DisplayName,
                plan = plan?.Name,
                used_download_bytes = user.UsedDownloadBytes,
                used_upload_bytes = user.UsedUploadBytes,
                used_total_bytes = user.UsedTotalBytes,
                used_seconds = user.UsedSeconds,
                remaining_bytes = quota?.RemainingBytes,
                remaining_seconds = quota?.RemainingTime?.TotalSeconds,
                expires_utc = quota?.ExpiresUtc,
            }, JsonOptions);
        });
        app.MapPost("/api/v1/users", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CreateUserBody>(request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
                return Results.Json(new { error = "username_and_password_required" }, JsonOptions, statusCode: 400);
            if (_store.UsernameExists(body.Username, 0))
                return Results.Json(new { error = "username_taken" }, JsonOptions, statusCode: 409);
            var planId = body.PlanId ?? _store.GetPlans().FirstOrDefault()?.Id ?? 0;
            if (_store.GetPlan(planId) is null)
                return Results.Json(new { error = "plan_missing" }, JsonOptions, statusCode: 400);

            var user = _store.SaveUser(new UserAccount
            {
                Username = body.Username.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? body.Username.Trim() : body.DisplayName.Trim(),
                PlanId = planId,
                Enabled = body.Enabled ?? true,
                Notes = body.Notes ?? "",
            }, body.Password);
            return Results.Json(UserDto(user), JsonOptions, statusCode: 201);
        });
        app.MapMethods("/api/v1/users/{username}", ["PATCH"], async (string username, HttpRequest request) =>
        {
            var user = _store.FindUserByName(username);
            if (user is null || user.IsGuest)
                return Results.Json(new { error = "not_found" }, JsonOptions, statusCode: 404);
            var body = await JsonSerializer.DeserializeAsync<PatchUserBody>(request.Body, JsonOptions) ?? new();
            if (body.Enabled is { } enabled)
                user.Enabled = enabled;
            if (body.PlanId is { } planId)
            {
                if (_store.GetPlan(planId) is null)
                    return Results.Json(new { error = "plan_missing" }, JsonOptions, statusCode: 400);
                user.PlanId = planId;
            }
            if (body.DisplayName is not null)
                user.DisplayName = body.DisplayName.Trim();
            if (body.Notes is not null)
                user.Notes = body.Notes;
            if (body.ResetUsage == true)
                _store.ResetUsage(user.Id);
            var password = string.IsNullOrWhiteSpace(body.Password) ? null : body.Password;
            var saved = _store.SaveUser(user, password);
            if (body.ResetUsage == true)
                saved = _store.GetUser(saved.Id) ?? saved;
            return Results.Json(UserDto(saved), JsonOptions);
        });

        await app.StartAsync();
        _app = app;
        BaseUrl = $"http://{FormatHost(bind)}:{settings.Port}";
        _log.Success($"Admin API listening on {BaseUrl}/api/v1/ (token auth).");
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stopping Admin API", ex);
        }
        _app = null;
        BaseUrl = "";
    }

    private object StatusDto() => new
    {
        gateway = _engine.State.ToString(),
        mode = _engine.Settings.Wifi.Mode.ToString(),
        started_utc = _engine.StartedUtc,
        uptime_seconds = _engine.StartedUtc is { } s ? (long)(DateTime.UtcNow - s).TotalSeconds : 0,
        gateway_address = _engine.GatewayAddress?.ToString(),
        sessions = _engine.Sessions.Count,
        wan = _engine.WanName,
        api = BaseUrl,
    };

    private object UserDto(UserAccount user)
    {
        var plan = _store.GetPlan(user.PlanId);
        var live = LiveUsage(user.Id);
        return new
        {
            id = user.Id,
            username = user.Username,
            display_name = user.DisplayName,
            enabled = user.Enabled,
            plan_id = user.PlanId,
            plan = plan?.Name,
            used_download_bytes = user.UsedDownloadBytes + live.Down,
            used_upload_bytes = user.UsedUploadBytes + live.Up,
            used_total_bytes = user.UsedTotalBytes + live.Down + live.Up,
            used_seconds = user.UsedSeconds + live.Sec,
        paused_until_utc = user.PausedUntilUtc,
            first_login_utc = user.FirstLoginUtc,
            created_utc = user.CreatedUtc,
            notes = user.Notes,
            online = live.Online,
        };
    }

    private object UserDetailDto(UserAccount user)
    {
        var baseDto = UserDto(user);
        var sessions = _engine.Sessions.Sessions.Where(s => s.User.Id == user.Id).Select(SessionDto).ToList();
        return new
        {
            user = baseDto,
            sessions,
        };
    }

    private static object HistoryDto(SessionHistoryEntry entry) => new
    {
        id = entry.Id,
        username = entry.Username,
        ip = entry.Ip,
        mac = entry.Mac,
        started_utc = entry.StartedUtc,
        ended_utc = entry.EndedUtc,
        download_bytes = entry.DownloadBytes,
        upload_bytes = entry.UploadBytes,
        end_reason = entry.EndReason,
    };

    private static object PlanDto(Plan plan) => new
    {
        id = plan.Id,
        name = plan.Name,
        data_limit_mb = plan.DataLimitMb,
        time_limit_minutes = plan.TimeLimitMinutes,
        session_limit_minutes = plan.SessionLimitMinutes,
        validity_days = plan.ValidityDays,
        download_kbps = plan.DownloadKbps,
        upload_kbps = plan.UploadKbps,
        max_devices = plan.MaxDevices,
        site_filter = plan.SiteFilter.ToString(),
        site_list = plan.SiteList,
        use_proxy = plan.UseUpstreamProxy,
    };

    private static Plan ApplyPlan(Plan plan, PlanBody body)
    {
        if (!string.IsNullOrWhiteSpace(body.Name))
            plan.Name = body.Name.Trim();
        if (body.DataLimitMb is { } data)
            plan.DataLimitMb = data;
        if (body.TimeLimitMinutes is { } time)
            plan.TimeLimitMinutes = time;
        if (body.SessionLimitMinutes is { } session)
            plan.SessionLimitMinutes = session;
        if (body.ValidityDays is { } days)
            plan.ValidityDays = days;
        if (body.DownloadKbps is { } down)
            plan.DownloadKbps = down;
        if (body.UploadKbps is { } up)
            plan.UploadKbps = up;
        if (body.MaxDevices is { } devices)
            plan.MaxDevices = devices;
        if (!string.IsNullOrWhiteSpace(body.SiteFilter) && Enum.TryParse<SiteFilterMode>(body.SiteFilter, true, out var filter))
            plan.SiteFilter = filter;
        if (body.SiteList is not null)
            plan.SiteList = body.SiteList;
        if (body.UseProxy is { } proxy)
            plan.UseUpstreamProxy = proxy;
        return plan;
    }

    private static object SessionDto(ClientSession session) => new
    {
        username = session.User.Username,
        ip = IpUtil.Format(session.Ip),
        mac = session.Mac,
        started_utc = session.StartedUtc,
        download_bytes = session.DownloadBytes,
        upload_bytes = session.UploadBytes,
        download_kbps = session.DownloadKbps,
        upload_kbps = session.UploadKbps,
        ap = session.ApName,
    };

    private (long Down, long Up, long Sec, bool Online) LiveUsage(long userId)
    {
        long down = 0, up = 0, sec = 0;
        var online = false;
        foreach (var session in _engine.Sessions.Sessions.Where(s => s.User.Id == userId))
        {
            online = true;
            down += Math.Max(0, session.DownloadBytes - session.FlushedDownload);
            up += Math.Max(0, session.UploadBytes - session.FlushedUpload);
            sec += Math.Max(0, (long)(DateTime.UtcNow - session.StartedUtc).TotalSeconds - session.FlushedSeconds);
        }
        return (down, up, sec, online);
    }

    private static bool IsAuthorized(HttpContext ctx, string token)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return FixedEquals(value, token);
        }
        if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var key))
            return FixedEquals(key.ToString().Trim(), token);
        return false;
    }

    private static bool FixedEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string FormatHost(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

    private static Task WriteJson(HttpContext ctx, object value)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, JsonOptions);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class AuthVerifyBody
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    private sealed class CreateUserBody
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? DisplayName { get; set; }
        public long? PlanId { get; set; }
        public bool? Enabled { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class PauseBody
    {
        public DateTime? Until { get; set; }
    }

    private sealed class PlanBody
    {
        public string? Name { get; set; }
        public long? DataLimitMb { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public int? SessionLimitMinutes { get; set; }
        public int? ValidityDays { get; set; }
        public int? DownloadKbps { get; set; }
        public int? UploadKbps { get; set; }
        public int? MaxDevices { get; set; }
        public string? SiteFilter { get; set; }
        public string? SiteList { get; set; }
        public bool? UseProxy { get; set; }
    }

    private sealed class PatchUserBody
    {
        public bool? Enabled { get; set; }
        public long? PlanId { get; set; }
        public string? DisplayName { get; set; }
        public string? Notes { get; set; }
        public string? Password { get; set; }
        public bool? ResetUsage { get; set; }
    }
}
