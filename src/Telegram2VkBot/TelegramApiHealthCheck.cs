using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Telegram2VkBot;

/// <summary>
/// Проверка доступности Telegram Bot API (getMe).
/// </summary>
public sealed class TelegramApiHealthCheck : IHealthCheck
{
    public const string HttpClientName = "TelegramHealth";

    private readonly TelegramOptions _telegram;
    private readonly IHttpClientFactory _httpFactory;

    public TelegramApiHealthCheck(IOptions<TelegramOptions> telegram, IHttpClientFactory httpFactory)
    {
        _telegram = telegram.Value;
        _httpFactory = httpFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_telegram.BotToken))
            return HealthCheckResult.Unhealthy("TELEGRAM__BotToken не задан.");

        var url = $"https://api.telegram.org/bot{_telegram.BotToken}/getMe";
        var http = _httpFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy(
                    $"Telegram API HTTP {(int)response.StatusCode}",
                    data: new Dictionary<string, object> { ["body"] = Truncate(json, 500) });
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : json;
                return HealthCheckResult.Unhealthy($"Telegram getMe: ok=false, {desc}");
            }

            var username = doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("username", out var un)
                ? un.GetString()
                : null;

            return HealthCheckResult.Healthy(
                username is null ? "Telegram API доступен." : $"Telegram API: @{username}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Не удалось связаться с Telegram API.", ex);
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return string.Concat(s.AsSpan(0, max), "…");
    }
}
