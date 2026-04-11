using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TelegramPanel.Web.Services;

public sealed class AppSelfUpdateService
{
    private const string CacheKeyPrefix = "self-update:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpdateCheckOptions> _updateOptions;
    private readonly IOptionsMonitor<SelfUpdateOptions> _selfUpdateOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly AppRestartService _restartService;
    private readonly ILogger<AppSelfUpdateService> _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public AppSelfUpdateService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<UpdateCheckOptions> updateOptions,
        IOptionsMonitor<SelfUpdateOptions> selfUpdateOptions,
        IWebHostEnvironment environment,
        AppRestartService restartService,
        ILogger<AppSelfUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _updateOptions = updateOptions;
        _selfUpdateOptions = selfUpdateOptions;
        _environment = environment;
        _restartService = restartService;
        _logger = logger;
    }

    public bool IsDockerEnvironment => IsRunningInDocker();

    public async Task<AppSelfUpdateInfo> CheckLatestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionService.Version;
        var updateOptions = _updateOptions.CurrentValue;
        var selfUpdateOptions = _selfUpdateOptions.CurrentValue;
        var isDocker = IsRunningInDocker();

        if (!selfUpdateOptions.Enabled)
        {
            return AppSelfUpdateInfo.Disabled(currentVersion, isDocker, "自动更新功能未启用");
        }

        var repo = (updateOptions.Repository ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
        {
            return AppSelfUpdateInfo.Failed(currentVersion, isDocker, "UpdateCheck:Repository 配置无效（应为 owner/repo）");
        }

        if (!TryGetAssetKeyword(RuntimeInformation.ProcessArchitecture, out var assetKeyword, out var archLabel))
        {
            return AppSelfUpdateInfo.Failed(currentVersion, isDocker, $"当前架构不支持自动更新：{RuntimeInformation.ProcessArchitecture}");
        }

        var cacheKey = $"{CacheKeyPrefix}{repo}:{assetKeyword}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is AppSelfUpdateInfo cached)
            return cached;

        AppSelfUpdateInfo result;
        try
        {
            var release = await FetchLatestReleaseAsync(repo, cancellationToken);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, "仓库未找到可用 Release，无法执行一键更新");
            }
            else
            {
                var latestVersion = ParseVersionFromTag(release.TagName);
                if (latestVersion == null)
                {
                    result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, $"解析版本失败（tag={release.TagName}）");
                }
                else
                {
                    var updateAvailable = CompareVersions(latestVersion.Value, currentVersion) > 0;
                    var matchedAsset = PickAsset(release.Assets, assetKeyword);
                    var blockedReason = ResolveBlockedReason(
                        updateAvailable: updateAvailable,
                        assetFound: matchedAsset != null,
                        isDocker: isDocker,
                        dockerOnly: selfUpdateOptions.DockerOnly,
                        archLabel: archLabel);

                    var notes = NormalizeNotes(release.Body);

                    result = new AppSelfUpdateInfo
                    {
                        Success = true,
                        Enabled = true,
                        CurrentVersion = currentVersion,
                        LatestTag = release.TagName,
                        LatestVersion = latestVersion.Value.ToString(),
                        UpdateAvailable = updateAvailable,
                        Url = release.HtmlUrl,
                        PublishedAt = release.PublishedAt,
                        Notes = notes,
                        IsDocker = isDocker,
                        AssetName = matchedAsset?.Name,
                        AssetSizeBytes = matchedAsset?.Size,
                        AssetDownloadUrl = matchedAsset?.BrowserDownloadUrl,
                        CanApply = blockedReason == null,
                        BlockedReason = blockedReason,
                        CheckedAtUtc = DateTimeOffset.UtcNow
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self update check failed");
            result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, ex.Message);
        }

        var cacheMinutes = updateOptions.CacheMinutes <= 0 ? 30 : updateOptions.CacheMinutes;
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes)
        });

        return result;
    }

    public async Task<AppSelfUpdateApplyResult> ApplyLatestAsync(CancellationToken cancellationToken = default)
    {
        var options = _selfUpdateOptions.CurrentValue;
        if (!options.Enabled)
            return AppSelfUpdateApplyResult.Failed("自动更新功能未启用");

        if (options.DockerOnly && !IsRunningInDocker())
            return AppSelfUpdateApplyResult.Failed("当前不是 Docker 运行环境，已禁止一键更新");

        if (!await _applyLock.WaitAsync(0, cancellationToken))
            return AppSelfUpdateApplyResult.Failed("已有更新任务在执行，请稍后重试");

        try
        {
            var check = await CheckLatestAsync(forceRefresh: true, cancellationToken);
            if (!check.Success)
                return AppSelfUpdateApplyResult.Failed($"检查更新失败：{check.Error}");

            if (!check.UpdateAvailable)
                return AppSelfUpdateApplyResult.Failed($"当前已是最新版本（v{check.CurrentVersion}）");

            if (!check.CanApply || string.IsNullOrWhiteSpace(check.AssetDownloadUrl) || string.IsNullOrWhiteSpace(check.AssetName))
            {
                var reason = check.BlockedReason ?? "未找到匹配当前架构的更新包";
                return AppSelfUpdateApplyResult.Failed(reason);
            }

            var workRoot = ResolveWorkRoot(options);
            Directory.CreateDirectory(workRoot);

            var workspaceDir = Path.Combine(workRoot, options.WorkDirectoryName.Trim().Length == 0 ? "self-update" : options.WorkDirectoryName.Trim());
            var currentDir = Path.Combine(workRoot, options.CurrentDirectoryName.Trim().Length == 0 ? "app-current" : options.CurrentDirectoryName.Trim());
            var backupDir = Path.Combine(workRoot, options.BackupDirectoryName.Trim().Length == 0 ? "app-previous" : options.BackupDirectoryName.Trim());
            Directory.CreateDirectory(workspaceDir);

            var stageDir = Path.Combine(workspaceDir, $"stage-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            var packageFileName = $"pkg-{Guid.NewGuid():N}-{SanitizeFileName(check.AssetName)}";
            var packagePath = Path.Combine(workspaceDir, packageFileName);

            try
            {
                await DownloadFileAsync(check.AssetDownloadUrl, packagePath, options.DownloadTimeoutSeconds, cancellationToken);

                Directory.CreateDirectory(stageDir);
                ZipFile.ExtractToDirectory(packagePath, stageDir, overwriteFiles: true);

                var entryDll = Path.Combine(stageDir, "TelegramPanel.Web.dll");
                if (!File.Exists(entryDll))
                    return AppSelfUpdateApplyResult.Failed("更新包结构无效：缺少 TelegramPanel.Web.dll");

                PromoteCurrentDirectory(stageDir, currentDir, backupDir);
                stageDir = string.Empty;

                var restartDelaySeconds = options.RestartDelaySeconds;
                if (restartDelaySeconds <= 0) restartDelaySeconds = 2;
                _restartService.RequestRestart(TimeSpan.FromSeconds(restartDelaySeconds), $"self-update {check.LatestTag}");

                _logger.LogWarning(
                    "Self update applied successfully. current={CurrentVersion} target={TargetTag} asset={AssetName}",
                    check.CurrentVersion,
                    check.LatestTag,
                    check.AssetName);

                return AppSelfUpdateApplyResult.Succeeded(
                    $"更新包已部署：{check.AssetName}，服务将在约 {restartDelaySeconds} 秒后重启",
                    check.LatestTag,
                    check.LatestVersion);
            }
            finally
            {
                TryDeleteFile(packagePath);
                if (!string.IsNullOrWhiteSpace(stageDir))
                    TryDeleteDirectory(stageDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply self update failed");
            return AppSelfUpdateApplyResult.Failed($"更新失败：{ex.Message}");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<GitHubReleaseDto?> FetchLatestReleaseAsync(string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        var client = CreateGitHubClient();

        using var resp = await client.GetAsync(url, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);
    }

    private async Task DownloadFileAsync(
        string url,
        string path,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var timeout = timeoutSeconds <= 0 ? 300 : timeoutSeconds;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telegram-Panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");

        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cts.Token);
    }

    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telegram-Panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private string ResolveWorkRoot(SelfUpdateOptions options)
    {
        var configured = (options.WorkRootPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured))
                return configured;

            return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));
        }

        if (Directory.Exists("/data"))
            return "/data";

        return _environment.ContentRootPath;
    }

    private static void PromoteCurrentDirectory(string stageDir, string currentDir, string backupDir)
    {
        if (Directory.Exists(backupDir))
            Directory.Delete(backupDir, recursive: true);

        var movedCurrent = false;
        try
        {
            if (Directory.Exists(currentDir))
            {
                Directory.Move(currentDir, backupDir);
                movedCurrent = true;
            }

            Directory.Move(stageDir, currentDir);
        }
        catch
        {
            if (movedCurrent && Directory.Exists(backupDir) && !Directory.Exists(currentDir))
            {
                Directory.Move(backupDir, currentDir);
            }

            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = fileName.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (invalidChars.Contains(buffer[i]))
                buffer[i] = '_';
        }

        return new string(buffer);
    }

    private static string? NormalizeNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var notes = body.Trim();
        if (notes.Length > 2000)
            notes = notes[..2000].Trim() + "\n…";

        return notes;
    }

    private static GitHubAssetDto? PickAsset(IReadOnlyList<GitHubAssetDto>? assets, string assetKeyword)
    {
        if (assets == null || assets.Count == 0)
            return null;

        return assets
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Where(x => x.Name!.Contains(assetKeyword, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Name!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.BrowserDownloadUrl))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolveBlockedReason(bool updateAvailable, bool assetFound, bool isDocker, bool dockerOnly, string archLabel)
    {
        if (!updateAvailable)
            return "当前已是最新版本";
        if (!assetFound)
            return $"未找到匹配架构 {archLabel} 的更新包";
        if (dockerOnly && !isDocker)
            return "当前不是 Docker 运行环境";
        return null;
    }

    private static bool TryGetAssetKeyword(Architecture architecture, out string keyword, out string archLabel)
    {
        switch (architecture)
        {
            case Architecture.X64:
                keyword = "linux-x64";
                archLabel = "linux-x64";
                return true;
            case Architecture.Arm64:
                keyword = "linux-arm64";
                archLabel = "linux-arm64";
                return true;
            default:
                keyword = string.Empty;
                archLabel = architecture.ToString();
                return false;
        }
    }

    private static bool IsRunningInDocker()
    {
        var dotnetInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(dotnetInContainer, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        return File.Exists("/.dockerenv");
    }

    private static SimpleVersion? ParseVersionFromTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var s = tagName.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

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

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed class AppSelfUpdateInfo
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool Enabled { get; init; }

    public string CurrentVersion { get; init; } = "Unknown";
    public string? LatestVersion { get; init; }
    public string? LatestTag { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsDocker { get; init; }
    public bool CanApply { get; init; }
    public string? BlockedReason { get; init; }

    public string? AssetName { get; init; }
    public long? AssetSizeBytes { get; init; }
    public string? AssetDownloadUrl { get; init; }

    public static AppSelfUpdateInfo Disabled(string currentVersion, bool isDocker, string? reason = null) => new()
    {
        Success = true,
        Enabled = false,
        CurrentVersion = currentVersion,
        IsDocker = isDocker,
        UpdateAvailable = false,
        CanApply = false,
        BlockedReason = reason,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };

    public static AppSelfUpdateInfo Failed(string currentVersion, bool isDocker, string error) => new()
    {
        Success = false,
        Enabled = true,
        Error = error,
        CurrentVersion = currentVersion,
        IsDocker = isDocker,
        UpdateAvailable = false,
        CanApply = false,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
}

public sealed class AppSelfUpdateApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RestartScheduled { get; init; }
    public string? LatestTag { get; init; }
    public string? LatestVersion { get; init; }

    public static AppSelfUpdateApplyResult Failed(string message) => new()
    {
        Success = false,
        Message = message,
        RestartScheduled = false
    };

    public static AppSelfUpdateApplyResult Succeeded(string message, string? latestTag, string? latestVersion) => new()
    {
        Success = true,
        Message = message,
        RestartScheduled = true,
        LatestTag = latestTag,
        LatestVersion = latestVersion
    };
}
