using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroGatePilot.Core.Models;

namespace AeroGatePilot.Infrastructure.Api;

/// <summary>Fire-and-forget outbound webhook for usage / session events (external panels, CRM).</summary>
public sealed class WebhookClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly AppLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private WebhookSettings _settings = new();

    public WebhookClient(AppLog log) => _log = log;

    public void Configure(WebhookSettings settings) => _settings = settings ?? new();

    public void NotifyFlush(string username, long downloadBytes, long uploadBytes, long usedTotal, string? ip, string? mac)
    {
        if (!_settings.Enabled || !_settings.OnFlush || string.IsNullOrWhiteSpace(_settings.Url))
            return;
        _ = SendAsync(new
        {
            @event = "usage_flush",
            username,
            download_bytes = downloadBytes,
            upload_bytes = uploadBytes,
            used_total = usedTotal,
            session = ip is null ? null : new { ip, mac },
            timestamp = DateTime.UtcNow,
        });
    }

    public void NotifySessionEnd(string username, long downloadBytes, long uploadBytes, long usedTotal, string ip, string mac, string reason)
    {
        if (!_settings.Enabled || !_settings.OnSessionEnd || string.IsNullOrWhiteSpace(_settings.Url))
            return;
        _ = SendAsync(new
        {
            @event = "session_end",
            username,
            download_bytes = downloadBytes,
            upload_bytes = uploadBytes,
            used_total = usedTotal,
            session = new { ip, mac, reason },
            timestamp = DateTime.UtcNow,
        });
    }

    private async Task SendAsync(object payload)
    {
        var url = _settings.Url.Trim();
        var secret = _settings.Secret ?? "";
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(secret))
                {
                    var sig = Sign(body, secret);
                    request.Headers.TryAddWithoutValidation("X-AeroGate-Signature", sig);
                }
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AeroGatePilot", "1.1"));
                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return;
                _log.Warn($"Webhook {url} returned {(int)response.StatusCode} (attempt {attempt}).");
            }
            catch (Exception ex)
            {
                _log.Warn($"Webhook attempt {attempt} failed: {ex.Message}");
            }
            await Task.Delay(400 * attempt);
        }
    }

    private static string Sign(string body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
