using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 账号频道/群组数据自动同步（后台定时）。
/// </summary>
public sealed class AccountDataAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AccountDataAutoSyncBackgroundService> _logger;

    public AccountDataAutoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<AccountDataAutoSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 延迟一点，避免与启动时 DB 迁移抢资源
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = _configuration.GetValue("Sync:AutoSyncEnabled", false);
            var hours = _configuration.GetValue("Sync:IntervalHours", 6);
            if (hours < 1) hours = 1;
            if (hours > 24) hours = 24;

            if (!enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            var nowUtc = DateTime.UtcNow;
            var lastUtc = ReadLastAutoSyncAtUtcFromConfig();
            if (lastUtc.HasValue)
            {
                var nextUtc = lastUtc.Value.AddHours(hours);
                if (nextUtc > nowUtc)
                {
                    var delay = nextUtc - nowUtc;
                    if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dataSync = scope.ServiceProvider.GetRequiredService<DataSyncService>();

                _logger.LogInformation("Account auto sync started");
                var summary = await dataSync.SyncAllActiveAccountsAsync(stoppingToken);
                if (summary.AccountFailures.Count > 0)
                {
                    foreach (var f in summary.AccountFailures)
                        _logger.LogWarning("Account auto sync failed: {Phone} {Error}", f.Phone, f.Error);
                }

                _logger.LogInformation("Account auto sync completed: {Channels} channels, {Groups} groups (failures={Failures})",
                    summary.TotalChannelsSynced, summary.TotalGroupsSynced, summary.AccountFailures.Count);

                // 记录“上次自动同步时间”，用于容器重启后继续按间隔调度，避免“重启即跑一轮”导致限流。
                await PersistLastAutoSyncAtUtcAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account auto sync failed");

                // 失败时做一次短暂退避，避免重启/故障时进入高频重试导致更严重的限流。
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private DateTime? ReadLastAutoSyncAtUtcFromConfig()
    {
        var raw = (_configuration["Sync:LastAutoSyncAtUtc"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            if (parsed.Kind == DateTimeKind.Unspecified)
                parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private async Task PersistLastAutoSyncAtUtcAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        try
        {
            var path = LocalConfigFile.ResolvePath(_configuration, _environment);
            await LocalConfigFile.EnsureExistsAsync(path, cancellationToken);

            JsonObject root;
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }

            var sync = EnsureObject(root, "Sync");
            sync["LastAutoSyncAtUtc"] = utcNow.ToString("O", CultureInfo.InvariantCulture);

            var updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await LocalConfigFile.WriteJsonAtomicallyAsync(path, updatedJson, cancellationToken);
        }
        catch (Exception ex)
        {
            // 该信息仅用于优化调度，写失败不应影响同步主流程
            _logger.LogDebug(ex, "Failed to persist Sync:LastAutoSyncAtUtc (ignored)");
        }
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is JsonObject obj)
            return obj;

        var created = new JsonObject();
        root[key] = created;
        return created;
    }
}

