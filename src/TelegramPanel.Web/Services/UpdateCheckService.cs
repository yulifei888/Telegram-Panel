using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TelegramPanel.Web.Services;

public sealed class UpdateCheckService
{
    private const string CacheKeyPrefix = "update-check:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpdateCheckOptions> _options;
    private readonly ILogger<UpdateCheckService> _logger;

    public UpdateCheckService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<UpdateCheckOptions> options,
        ILogger<UpdateCheckService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<UpdateCheckInfo> GetLatestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionService.Version;
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return UpdateCheckInfo.Disabled(currentVersion);

        var repo = (options.Repository ?? "").Trim();
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            return UpdateCheckInfo.Failed(currentVersion, "UpdateCheck:Repository 配置无效（应为 owner/repo）");

        var cacheKey = $"{CacheKeyPrefix}{repo}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is UpdateCheckInfo cached)
            return cached;

        UpdateCheckInfo result;
        try
        {
            var release = await TryFetchLatestReleaseAsync(repo, cancellationToken);
            if (release != null)
            {
                result = BuildResult(currentVersion, release.TagName, release.HtmlUrl, release.PublishedAt, release.Body, source: "release");
            }
            else
            {
                var tag = await TryFetchLatestTagAsync(repo, cancellationToken);
                result = tag != null
                    ? BuildResult(currentVersion, tag.TagName, tag.HtmlUrl, publishedAt: null, body: null, source: "tag")
                    : UpdateCheckInfo.Failed(currentVersion, "未获取到任何 Release/Tag（仓库可能未发布 Release）");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            result = UpdateCheckInfo.Failed(currentVersion, ex.Message);
        }

        var cacheMinutes = options.CacheMinutes <= 0 ? 30 : options.CacheMinutes;
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes)
        });

        return result;
    }

    private UpdateCheckInfo BuildResult(
        string currentVersion,
        string? tagName,
        string? htmlUrl,
        DateTimeOffset? publishedAt,
        string? body,
        string source)
    {
        var latestVersion = ParseVersionFromTag(tagName);
        if (latestVersion == null)
        {
            return UpdateCheckInfo.Failed(currentVersion, $"解析版本失败（tag={tagName ?? "-" }）");
        }

        var updateAvailable = CompareVersions(latestVersion.Value, currentVersion) > 0;
        var notes = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        if (notes != null && notes.Length > 2000)
            notes = notes[..2000].Trim() + "\n…";

        return new UpdateCheckInfo
        {
            Success = true,
            Source = source,
            CurrentVersion = currentVersion,
            LatestTag = tagName,
            LatestVersion = latestVersion.Value.ToString(),
            UpdateAvailable = updateAvailable,
            Url = htmlUrl,
            PublishedAt = publishedAt,
            Notes = notes,
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task<GitHubReleaseDto?> TryFetchLatestReleaseAsync(string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        var client = CreateGitHubClient();

        using var resp = await client.GetAsync(url, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);
        return dto == null || string.IsNullOrWhiteSpace(dto.TagName) ? null : dto;
    }

    private async Task<GitHubTagDto?> TryFetchLatestTagAsync(string repo, CancellationToken cancellationToken)
    {
        // tags API 返回按“最近提交”排序的 tag，不一定等同于最新语义版本；但作为未发布 Release 的兜底够用
        var url = $"https://api.github.com/repos/{repo}/tags?per_page=1";
        var client = CreateGitHubClient();

        using var resp = await client.GetAsync(url, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        var list = JsonSerializer.Deserialize<List<GitHubTagApiDto>>(json, JsonOptions);
        var first = list?.FirstOrDefault();
        if (first == null || string.IsNullOrWhiteSpace(first.Name))
            return null;

        var htmlUrl = $"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(first.Name)}";
        return new GitHubTagDto(first.Name, htmlUrl);
    }

    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telegram-Panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static SimpleVersion? ParseVersionFromTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var s = tagName.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        // 去掉 -prerelease 与 +metadata
        var dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        var plus = s.IndexOf('+');
        if (plus > 0) s = s[..plus];

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;

        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;
        return new SimpleVersion(major, minor, patch);
    }

    private static int CompareVersions(SimpleVersion latest, string current)
    {
        var cur = ParseVersionFromTag(current) ?? new SimpleVersion(0, 0, 0);
        return latest.CompareTo(cur);
    }

    private readonly record struct SimpleVersion(int Major, int Minor, int Patch) : IComparable<SimpleVersion>
    {
        public int CompareTo(SimpleVersion other)
        {
            var r = Major.CompareTo(other.Major);
            if (r != 0) return r;
            r = Minor.CompareTo(other.Minor);
            if (r != 0) return r;
            return Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private sealed class GitHubTagApiDto
    {
        public string? Name { get; set; }
    }

    private sealed record GitHubTagDto(string TagName, string HtmlUrl);
}

public sealed class UpdateCheckInfo
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public string Source { get; init; } = "-";
    public string CurrentVersion { get; init; } = "Unknown";
    public string? LatestVersion { get; init; }
    public string? LatestTag { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static UpdateCheckInfo Disabled(string currentVersion) => new()
    {
        Success = true,
        Source = "disabled",
        CurrentVersion = currentVersion,
        UpdateAvailable = false,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };

    public static UpdateCheckInfo Failed(string currentVersion, string error) => new()
    {
        Success = false,
        Error = error,
        Source = "error",
        CurrentVersion = currentVersion,
        UpdateAvailable = false,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
}
