using Microsoft.Extensions.Hosting;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Web.Services;

/// <summary>
/// Webhook 注册后台服务：启动时自动为所有活跃 Bot 注册 Webhook。
/// </summary>
public sealed class WebhookRegistrationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhookRegistrationService> _logger;

    public WebhookRegistrationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<WebhookRegistrationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 等待应用完全启动
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            // 检查是否启用 Webhook 模式
            var webhookEnabled = string.Equals(
                _configuration["Telegram:WebhookEnabled"]?.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (!webhookEnabled)
            {
                _logger.LogInformation("Webhook mode disabled, using polling mode");
                return;
            }

            var baseUrl = (_configuration["Telegram:WebhookBaseUrl"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.LogWarning("Webhook enabled but WebhookBaseUrl not configured, skipping webhook registration");
                return;
            }

            var secretToken = (_configuration["Telegram:WebhookSecretToken"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(secretToken))
            {
                _logger.LogWarning("Webhook enabled but WebhookSecretToken not configured, skipping webhook registration");
                return;
            }

            _logger.LogInformation("Webhook mode enabled, registering webhooks for all active bots...");

            await RegisterWebhooksAsync(baseUrl, secretToken, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // ignore
        }
        catch (Exception ex)
        {
            // 默认 HostOptions: BackgroundServiceExceptionBehavior=StopHost
            // 这里兜底防止“注册 webhook 失败”把整个站点带崩导致需要重启才能访问。
            _logger.LogError(ex, "Webhook registration background service failed");
        }
    }

    private async Task RegisterWebhooksAsync(string baseUrl, string secretToken, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
        var botApi = scope.ServiceProvider.GetRequiredService<TelegramBotApiClient>();

        var bots = await botRepo.GetAllAsync();
        var activeBots = bots.Where(b => b.IsActive && !string.IsNullOrWhiteSpace(b.Token)).ToList();

        if (activeBots.Count == 0)
        {
            _logger.LogInformation("No active bots found, nothing to register");
            return;
        }

        _logger.LogInformation("Found {Count} active bots to register webhooks", activeBots.Count);

        foreach (var bot in activeBots)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var token = bot.Token!.Trim();

                // 构建 Webhook URL：baseUrl + /api/bot/webhook/{SHA256(token)}
                // 说明：避免在反向代理/access log 中泄露真实 bot token。
                var pathToken = WebhookTokenHelper.ToWebhookPathToken(token);
                var webhookUrl = $"{baseUrl.TrimEnd('/')}/api/bot/webhook/{pathToken}";

                await botApi.SetWebhookAsync(
                    token: token,
                    url: webhookUrl,
                    secretToken: secretToken,
                    allowedUpdates: BotUpdateHub.AllowedUpdatesJson,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Webhook registered for bot {BotName} (id={BotId})", bot.Name, bot.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register webhook for bot {BotName} (id={BotId})", bot.Name, bot.Id);
            }
        }

        _logger.LogInformation("Webhook registration completed");
    }
}
