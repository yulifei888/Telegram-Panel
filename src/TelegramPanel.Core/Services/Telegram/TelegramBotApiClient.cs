using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TelegramPanel.Core.Services.Telegram;

public class TelegramBotApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TelegramBotApiClient> _logger;

    public TelegramBotApiClient(HttpClient http, ILogger<TelegramBotApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<JsonElement> CallAsync(string token, string method, IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken)
    {
        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Bot Token 为空");

        method = (method ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(method))
            throw new InvalidOperationException("method 为空");

        var url = BuildUrl(token, method, query);
        using var resp = await _http.GetAsync(url, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var code = root.TryGetProperty("error_code", out var codeEl) ? codeEl.GetInt32() : 0;
            var desc = root.TryGetProperty("description", out var descEl) ? descEl.GetString() : "未知错误";
            throw new InvalidOperationException($"Bot API 调用失败：{method} ({code}) {desc}");
        }

        if (!root.TryGetProperty("result", out var result))
            throw new InvalidOperationException($"Bot API 返回缺少 result：{method}");

        return result.Clone();
    }

    private static string BuildUrl(string token, string method, IReadOnlyDictionary<string, string?> query)
    {
        var sb = new StringBuilder();
        sb.Append("https://api.telegram.org/bot");
        sb.Append(Uri.EscapeDataString(token));
        sb.Append('/');
        sb.Append(Uri.EscapeDataString(method));

        if (query.Count == 0)
            return sb.ToString();

        var first = true;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }
}

