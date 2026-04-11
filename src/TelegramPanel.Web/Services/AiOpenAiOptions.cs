namespace TelegramPanel.Web.Services;

public sealed class AiOpenAiOptions
{
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string? DefaultModel { get; set; }

    public List<string> PresetModels { get; set; } = new();

    public int RetryCount { get; set; } = 2;

    public AiOpenAiSettingsSnapshot ToSnapshot()
    {
        return AiOpenAiSettingsSnapshot.From(this);
    }
}

public sealed record AiOpenAiSettingsSnapshot(
    string? Endpoint,
    string? ApiKey,
    string? DefaultModel,
    IReadOnlyList<string> PresetModels,
    int RetryCount)
{
    public static AiOpenAiSettingsSnapshot Empty { get; } = new(null, null, null, Array.Empty<string>(), 2);

    public string CompletionsPath => "/chat/completions";

    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool IsConfigured => HasEndpoint && HasApiKey;

    public string? ResolveModel(string? overrideModel)
    {
        var preferred = NormalizeModel(overrideModel);
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return NormalizeModel(DefaultModel);
    }

    public string? GetCompletionsEndpoint()
    {
        if (!HasEndpoint)
            return null;

        return $"{Endpoint!.TrimEnd('/')}{CompletionsPath}";
    }

    public IReadOnlyList<string> GetSelectableModels()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        var defaultModel = NormalizeModel(DefaultModel);
        if (!string.IsNullOrWhiteSpace(defaultModel) && seen.Add(defaultModel))
            list.Add(defaultModel);

        foreach (var item in PresetModels)
        {
            var normalized = NormalizeModel(item);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (seen.Add(normalized))
                list.Add(normalized);
        }

        return list;
    }

    public bool TryValidateForTask(string? overrideModel, out string? error)
    {
        if (!HasEndpoint)
        {
            error = "未配置 AI 端点（AI:OpenAI:Endpoint）";
            return false;
        }

        var completionsEndpoint = GetCompletionsEndpoint();
        if (string.IsNullOrWhiteSpace(completionsEndpoint)
            || !Uri.TryCreate(completionsEndpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "AI 端点格式无效，请填写如 https://xxx.com/v1";
            return false;
        }

        if (!HasApiKey)
        {
            error = "未配置 AI Key（AI:OpenAI:ApiKey）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ResolveModel(overrideModel)))
        {
            error = "未配置 AI 模型：请在系统设置填写默认模型，或在任务里选择自定义模型";
            return false;
        }

        error = null;
        return true;
    }

    public static AiOpenAiSettingsSnapshot From(AiOpenAiOptions? options)
    {
        if (options == null)
            return Empty;

        var defaultModel = NormalizeModel(options.DefaultModel);
        var presetModels = NormalizeModelEntries(options.PresetModels, defaultModel);
        return new AiOpenAiSettingsSnapshot(
            NormalizeEndpoint(options.Endpoint),
            NormalizeApiKey(options.ApiKey),
            defaultModel,
            presetModels,
            NormalizeRetryCount(options.RetryCount));
    }

    public static string? NormalizeEndpoint(string? value)
    {
        var endpoint = (value ?? string.Empty).Trim();
        if (endpoint.Length == 0)
            return null;

        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = $"https://{endpoint.TrimStart('/')}";

        endpoint = endpoint.TrimEnd('/');

        var suffixes = new[]
        {
            "/chat/completions",
            "/responses",
            "/completions"
        };

        foreach (var suffix in suffixes)
        {
            if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        return endpoint.Length == 0 ? null : endpoint;
    }

    public static string? NormalizeApiKey(string? value)
    {
        var apiKey = (value ?? string.Empty).Trim();
        return apiKey.Length == 0 ? null : apiKey;
    }

    public static string? NormalizeModel(string? value)
    {
        var model = (value ?? string.Empty).Trim();
        return model.Length == 0 ? null : model;
    }

    public static int NormalizeRetryCount(int value)
    {
        if (value < 0)
            return 0;

        return value > 5 ? 5 : value;
    }

    public static List<string> NormalizeModelEntries(IEnumerable<string>? values, string? defaultModel = null)
    {
        var normalizedDefault = NormalizeModel(defaultModel);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in values ?? Array.Empty<string>())
        {
            var normalized = NormalizeModel(item);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!string.IsNullOrWhiteSpace(normalizedDefault)
                && string.Equals(normalized, normalizedDefault, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }
}
