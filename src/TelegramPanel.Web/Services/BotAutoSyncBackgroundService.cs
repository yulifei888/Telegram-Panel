using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Web.Services;

/// <summary>
/// Bot 频道自动同步（轮询），用于在 Bot 被拉进新频道后自动出现在列表里。
/// </summary>
public class BotAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BotAutoSyncBackgroundService> _logger;
    private readonly IConfiguration _configuration;

    public BotAutoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<BotAutoSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 默认开启：满足“Bot 拉进频道后自动出现”的直觉
        var enabled = _configuration.GetValue("Telegram:BotAutoSyncEnabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Bot auto sync disabled (Telegram:BotAutoSyncEnabled=false)");
            return;
        }

        // 秒级轮询（近似“监听”效果）；可通过配置调整
        var seconds = _configuration.GetValue("Telegram:BotAutoSyncIntervalSeconds", 5);
        if (seconds < 2) seconds = 2;
        if (seconds > 60) seconds = 60;
        var interval = TimeSpan.FromSeconds(seconds);

        _logger.LogInformation("Bot auto sync started, interval {IntervalSeconds} seconds", seconds);

        // 延迟一点，避免与启动时 DB 迁移抢资源
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bot auto sync loop failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var botManagement = scope.ServiceProvider.GetRequiredService<BotManagementService>();
        var botTelegram = scope.ServiceProvider.GetRequiredService<BotTelegramService>();

        var bots = (await botManagement.GetAllBotsAsync()).Where(b => b.IsActive).ToList();
        if (bots.Count == 0)
            return;

        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = await botTelegram.SyncBotChannelsAsync(bot.Id, cancellationToken);
                _logger.LogInformation("Bot auto sync: bot {BotId} synced {Count} channels", bot.Id, count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bot auto sync failed for bot {BotId}", bot.Id);
            }
        }
    }
}
