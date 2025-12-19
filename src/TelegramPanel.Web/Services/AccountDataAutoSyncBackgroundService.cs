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
    private readonly ILogger<AccountDataAutoSyncBackgroundService> _logger;

    public AccountDataAutoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<AccountDataAutoSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account auto sync failed");
            }

            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
        }
    }
}

