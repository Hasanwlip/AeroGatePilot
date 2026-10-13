using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Infrastructure.Data;

namespace AeroGatePilot.Infrastructure.Api;

/// <summary>
/// Queues webhook events in SQLite and retries them. Usage flushes replace the pending row for that user
/// so a down site does not grow one row per second.
/// </summary>
public sealed class WebhookClient : IAsyncDisposable
{
    private const int MaxAttempts = 24;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly AppLog _log;
    private readonly SqliteStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _drain = new(1, 1);
    private WebhookSettings _settings = new();

    public WebhookClient(AppLog log, SqliteStore store)
    {
        _log = log;
        _store = store;
    }

    public void Configure(WebhookSettings settings) => _settings = settings ?? new();

    public void NotifyFlush(string username, long downloadBytes, long uploadBytes, long usedTotal, string? ip, string? mac)
    {
        if (!_settings.Enabled || !_settings.OnFlush || string.IsNullOrWhiteSpace(_settings.Url))
            return;
        Enqueue("usage_flush", username, new
        {
            @event = "usage_flush",
            username,
            download_bytes = downloadBytes,
            upload_bytes = uploadBytes,
            used_total = usedTotal,
            session = ip is null ? null : new { ip, mac },
            timestamp = DateTime.UtcNow,
        }, replaceSameUser: true);
    }

    public void NotifySessionEnd(string username, long downloadBytes, long uploadBytes, long usedTotal, string ip, string mac, string reason)
    {
        if (!_settings.Enabled || !_settings.OnSessionEnd || string.IsNullOrWhiteSpace(_settings.Url))
            return;
        Enqueue("session_end", username, new
        {
            @event = "session_end",
            username,
            download_bytes = downloadBytes,
            upload_bytes = uploadBytes,
            used_total = usedTotal,
            session = new { ip, mac, reason },
            timestamp = DateTime.UtcNow,
        }, replaceSameUser: false);
    }

    public async Task DrainAsync()
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.Url))
            return;
        if (!await _drain.WaitAsync(0))
            return;
        try
        {
            foreach (var item in _store.TakeDueWebhooks())
                await SendOneAsync(item.Id, item.Payload, item.Attempts);
        }
        catch (Exception ex)
        {
            _log.Warn($"Webhook queue: {ex.Message}");
        }
        finally
        {
            _drain.Release();
        }
    }

    private void Enqueue(string eventName, string username, object payload, bool replaceSameUser)
    {
        try
        {
            _store.EnqueueWebhook(eventName, username, JsonSerializer.Serialize(payload, JsonOptions), replaceSameUser);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not queue webhook: {ex.Message}");
        }
    }

    private async Task SendOneAsync(long id, string body, int attempts)
    {
        var url = _settings.Url.Trim();
        var secret = _settings.Secret ?? "";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(secret))
                request.Headers.TryAddWithoutValidation("X-AeroGate-Signature", Sign(body, secret));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AeroGatePilot", "1.2"));
            using var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _store.DeleteWebhook(id);
                return;
            }
            Fail(id, attempts, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Fail(id, attempts, ex.Message);
        }
    }

    private void Fail(long id, int attempts, string error)
    {
        var next = attempts + 1;
        if (next >= MaxAttempts)
        {
            _log.Warn($"Webhook dropped after {next} tries: {error}");
            _store.DeleteWebhook(id);
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(next, 8))));
        _store.RescheduleWebhook(id, next, DateTime.UtcNow + delay, error.Length > 300 ? error[..300] : error);
    }

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _drain.Dispose();
        return ValueTask.CompletedTask;
    }
}
