using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class TelegramPanelAiService : ITelegramPanelAiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiOpenAiOptions> _optionsMonitor;
    private readonly ILogger<TelegramPanelAiService> _logger;

    public TelegramPanelAiService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiOpenAiOptions> optionsMonitor,
        ILogger<TelegramPanelAiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<TelegramPanelAiChooseActionResult> ChooseActionAsync(
        TelegramPanelAiChooseActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = _optionsMonitor.CurrentValue.ToSnapshot();
        if (!settings.TryValidateForTask(request.Model, out var validateError))
        {
            return new TelegramPanelAiChooseActionResult(
                Success: false,
                Mode: null,
                ButtonIndex: null,
                ReplyText: null,
                Reason: null,
                Error: validateError);
        }

        var model = settings.ResolveModel(request.Model)!;
        var buttons = request.Buttons ?? Array.Empty<TelegramPanelAiButtonOption>();
        var buttonLines = buttons.Select(x => $"{x.Index}. {x.Text}").ToArray();
        var hasButtons = buttonLines.Length > 0;
        var systemPrompt = hasButtons
            ? "你是 Telegram 验证助手。当前消息存在可点击按钮。请根据消息文本、按钮列表和可选图片，只输出唯一 JSON 决策。" +
              "只允许输出 JSON，不要输出 Markdown，不要解释。JSON 格式：" +
              "{\"mode\":\"click_button\",\"button_index\":0,\"reply_text\":null,\"reason\":\"30字内\"}。" +
              "注意：当前存在按钮时，不允许返回 reply_text，必须返回 click_button，button_index 必须是 0 基索引。"
            : "你是 Telegram 验证助手。请根据消息文本、按钮列表和可选图片，输出唯一 JSON 决策。" +
              "只允许输出 JSON，不要输出 Markdown，不要解释。JSON 格式：" +
              "{\"mode\":\"click_button\"|\"reply_text\",\"button_index\":0,\"reply_text\":null,\"reason\":\"30字内\"}。" +
              "如果需要点击按钮，mode=click_button，button_index 必须是 0 基索引；如果更适合直接回答，mode=reply_text，reply_text 必须填写最终回复内容。";

        var text = new StringBuilder();
        text.AppendLine("请处理下面这条 Telegram 验证消息。");
        if (!string.IsNullOrWhiteSpace(request.Context))
            text.AppendLine($"上下文：{request.Context.Trim()}");
        text.AppendLine($"消息文本：{(string.IsNullOrWhiteSpace(request.MessageText) ? "（空）" : request.MessageText.Trim())}");
        text.AppendLine(buttonLines.Length == 0
            ? "按钮列表：无"
            : $"按钮列表：{string.Join("；", buttonLines)}");
        text.AppendLine("要求：优先输出最可靠的结果；如果图片信息重要，请结合图片判断。只输出 JSON。 ");

        object userContent = request.Image is { Content.Length: > 0 }
            ? new object[]
            {
                new { type = "text", text = text.ToString() },
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{request.Image.MimeType};base64,{Convert.ToBase64String(request.Image.Content)}"
                    }
                }
            }
            : text.ToString();

        try
        {
            return await ExecuteWithRetryAsync(
                operationName: "验证决策",
                model: model,
                retryCount: settings.RetryCount,
                action: ct => ChooseActionCoreAsync(settings, model, systemPrompt, userContent, ct),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI choice response after retries");
            return new TelegramPanelAiChooseActionResult(false, null, null, null, null, $"AI 响应解析失败：{ex.Message}");
        }
    }

    public async Task<TelegramPanelAiReplyTextResult> ReplyTextAsync(
        TelegramPanelAiReplyTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = _optionsMonitor.CurrentValue.ToSnapshot();
        if (!settings.TryValidateForTask(request.Model, out var validateError))
            return new TelegramPanelAiReplyTextResult(false, null, validateError);

        var model = settings.ResolveModel(request.Model)!;
        var systemPrompt = (request.Prompt ?? string.Empty).Trim();
        if (systemPrompt.Length == 0)
            systemPrompt = "你是 Telegram 验证/答题助手。请只回复最终答案，不要解释，不要输出多余内容。";

        var text = new StringBuilder();
        text.AppendLine((request.Query ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(request.Context))
            text.AppendLine($"上下文：{request.Context.Trim()}");
        text.AppendLine("只回复最终答案。不要解释。不要添加引号。 ");

        object userContent = request.Image is { Content.Length: > 0 }
            ? new object[]
            {
                new { type = "text", text = text.ToString() },
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{request.Image.MimeType};base64,{Convert.ToBase64String(request.Image.Content)}"
                    }
                }
            }
            : text.ToString();

        try
        {
            return await ExecuteWithRetryAsync(
                operationName: "文本作答",
                model: model,
                retryCount: settings.RetryCount,
                action: async ct =>
                {
                    var content = await SendChatCompletionAsync(settings, model, systemPrompt, userContent, ct);
                    var normalized = StripMarkdownFence(content).Trim();
                    if (normalized.Length == 0)
                        throw new InvalidOperationException("AI 返回了空回复");

                    return new TelegramPanelAiReplyTextResult(true, normalized, null);
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI reply text after retries");
            return new TelegramPanelAiReplyTextResult(false, null, $"AI 请求失败：{ex.Message}");
        }
    }

    public async Task<(bool Success, string? Model, string? ResponseText, string? Error)> TestConnectionAsync(
        AiOpenAiSettingsSnapshot settings,
        string? overrideModel = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.TryValidateForTask(overrideModel, out var validateError))
            return (false, null, null, validateError);

        var model = settings.ResolveModel(overrideModel)!;

        try
        {
            var content = await ExecuteWithRetryAsync(
                operationName: "连通测试",
                model: model,
                retryCount: settings.RetryCount,
                action: ct => SendChatCompletionAsync(
                    settings,
                    model,
                    "你是 AI 连通性测试助手。请只回复 pong。",
                    "ping",
                    ct),
                cancellationToken: cancellationToken);

            var normalized = StripMarkdownFence(content).Trim();
            if (normalized.Length == 0)
                return (false, model, null, "AI 返回了空响应");

            return (true, model, TrimForPreview(normalized), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI connectivity test failed after retries");
            return (false, model, null, ex.Message);
        }
    }

    private async Task<TelegramPanelAiChooseActionResult> ChooseActionCoreAsync(
        AiOpenAiSettingsSnapshot settings,
        string model,
        string systemPrompt,
        object userContent,
        CancellationToken cancellationToken)
    {
        var content = await SendChatCompletionAsync(settings, model, systemPrompt, userContent, cancellationToken);
        var json = ExtractJsonObject(content);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var mode = root.TryGetProperty("mode", out var modeElement)
            ? (modeElement.GetString() ?? string.Empty).Trim()
            : string.Empty;
        var reason = root.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()
            : null;

        if (string.Equals(mode, "click_button", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("button_index", out var buttonIndexElement)
                || buttonIndexElement.ValueKind != JsonValueKind.Number
                || !buttonIndexElement.TryGetInt32(out var buttonIndex))
            {
                throw new InvalidOperationException("AI 返回了点击按钮模式，但缺少有效的 button_index");
            }

            return new TelegramPanelAiChooseActionResult(true, "click_button", buttonIndex, null, reason, null);
        }

        if (string.Equals(mode, "reply_text", StringComparison.OrdinalIgnoreCase))
        {
            var replyText = root.TryGetProperty("reply_text", out var replyElement)
                ? (replyElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (replyText.Length == 0)
                throw new InvalidOperationException("AI 返回了 reply_text 模式，但 reply_text 为空");

            return new TelegramPanelAiChooseActionResult(true, "reply_text", null, replyText, reason, null);
        }

        throw new InvalidOperationException($"AI 返回了未知模式：{mode}");
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        string model,
        int retryCount,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var normalizedRetryCount = AiOpenAiSettingsSnapshot.NormalizeRetryCount(retryCount);
        var maxAttempts = Math.Max(1, normalizedRetryCount + 1);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= maxAttempts || !IsRetryableAiFailure(ex, cancellationToken))
                    break;

                var delay = GetRetryDelay(attempt);
                _logger.LogWarning(
                    ex,
                    "AI {OperationName} 失败，准备重试：第 {NextAttempt}/{MaxAttempts} 次，模型={Model}，等待 {DelayMs}ms",
                    operationName,
                    attempt + 1,
                    maxAttempts,
                    model,
                    (int)delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException($"AI {operationName} 失败");
    }

    private async Task<string> SendChatCompletionAsync(
        AiOpenAiSettingsSnapshot settings,
        string model,
        string systemPrompt,
        object userContent,
        CancellationToken cancellationToken)
    {
        var endpoint = settings.GetCompletionsEndpoint()
            ?? throw new InvalidOperationException("OpenAI 端点未配置");
        var apiKey = settings.ApiKey
            ?? throw new InvalidOperationException("OpenAI Key 未配置");

        var client = _httpClientFactory.CreateClient(nameof(TelegramPanelAiService));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            temperature = 0.1,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        });

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 请求失败（HTTP {(int)response.StatusCode}）：{TrimForError(body)}");

        try
        {
            using var document = JsonDocument.Parse(body);
            var contentElement = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");

            return contentElement.ValueKind switch
            {
                JsonValueKind.String => contentElement.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Concat(contentElement.EnumerateArray()
                    .Where(x => x.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty)),
                _ => throw new InvalidOperationException("AI 返回的 content 结构不受支持")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI completion response: {Body}", body);
            throw new InvalidOperationException($"AI 响应结构无效：{ex.Message}");
        }
    }

    private static bool IsRetryableAiFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return false;

        if (ex is HttpRequestException)
            return true;

        if (ex is TaskCanceledException)
            return true;

        var message = ex.Message ?? string.Empty;
        if (message.Contains("端点未配置", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Key 未配置", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未配置 AI", StringComparison.OrdinalIgnoreCase)
            || message.Contains("端点格式无效", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryGetHttpStatusCode(ex, out var statusCode))
            return statusCode == 408 || statusCode == 409 || statusCode == 429 || statusCode >= 500;

        return true;
    }

    private static bool TryGetHttpStatusCode(Exception ex, out int statusCode)
    {
        const string marker = "AI 请求失败（HTTP ";
        var message = ex.Message ?? string.Empty;
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            statusCode = 0;
            return false;
        }

        start += marker.Length;
        var end = message.IndexOf('）', start);
        if (end < 0)
            end = message.IndexOf(')', start);

        if (end <= start)
        {
            statusCode = 0;
            return false;
        }

        return int.TryParse(message[start..end], out statusCode);
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        var delayMs = Math.Min(3000, 800 * Math.Max(1, attempt));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static string ExtractJsonObject(string content)
    {
        var normalized = StripMarkdownFence(content).Trim();
        var start = normalized.IndexOf('{');
        var end = normalized.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("AI 未返回 JSON 对象");

        return normalized[start..(end + 1)];
    }

    private static string StripMarkdownFence(string content)
    {
        var text = (content ?? string.Empty).Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
            return text.Trim('`').Trim();

        var body = text[(firstLineEnd + 1)..];
        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? body[..lastFence].Trim() : body.Trim();
    }

    private static string TrimForError(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 400 ? normalized : normalized[..400];
    }

    private static string TrimForPreview(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }
}
