using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class BotSetAdminsTaskHandler : IModuleTaskHandler
{
    public string TaskType => BatchTaskTypes.BotSetAdmins;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<BotSetAdminsTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var botTelegram = host.Services.GetRequiredService<BotTelegramService>();

        var config = DeserializeConfig(host.Config);
        ValidateConfig(config);

        var channels = config.Channels
            .Where(x => x != null && x.TelegramId != 0)
            .GroupBy(x => x.TelegramId)
            .Select(x => x.First())
            .ToList();
        var channelIds = channels.Select(x => x.TelegramId).ToList();
        var channelTitleById = channels.ToDictionary(x => x.TelegramId, x => NormalizeChannelTitle(x));
        var userIds = config.UserIds.Where(x => x > 0).Distinct().ToList();

        if (channels.Count == 0 || userIds.Count == 0)
            throw new InvalidOperationException("任务缺少有效的频道或用户 ID");

        var completed = 0;
        var failed = 0;
        var failures = new List<BotAdminTaskFailureItem>();

        try
        {
            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    break;
                }

                var result = await botTelegram.PromoteChatMemberWithResultAsync(
                    botId: config.BotId,
                    channelTelegramIds: channelIds,
                    userId: userId,
                    rights: ConvertRights(config.Rights),
                    cancellationToken: cancellationToken);

                foreach (var (chatId, reason) in result.Failures)
                {
                    failures.Add(new BotAdminTaskFailureItem
                    {
                        ChannelTelegramId = chatId,
                        ChannelTitle = channelTitleById.TryGetValue(chatId, out var title) ? title : chatId.ToString(),
                        UserId = userId,
                        Reason = NormalizeReason(reason)
                    });
                }

                completed += result.SuccessCount + result.Failures.Count;
                failed += result.Failures.Count;
                await host.UpdateProgressAsync(completed, failed, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BotSetAdmins task failed (taskId={TaskId})", host.TaskId);
            config.Error = ex.Message;
            config.Failures = failures;
            config.FailureLines = BotAdminTaskFailureFormatter.BuildLines(failures);
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
            throw;
        }

        config.Failures = failures;
        config.FailureLines = BotAdminTaskFailureFormatter.BuildLines(failures);
        await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
    }

    private static BotSetAdminsTaskConfig DeserializeConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        try
        {
            return JsonSerializer.Deserialize<BotSetAdminsTaskConfig>(raw)
                   ?? throw new InvalidOperationException("任务 Config JSON 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }
    }

    private static void ValidateConfig(BotSetAdminsTaskConfig config)
    {
        if (config.BotId <= 0)
            throw new InvalidOperationException("任务 BotId 无效");
        if (config.Channels.Count == 0)
            throw new InvalidOperationException("任务缺少频道列表");
        if (config.UserIds.Count == 0)
            throw new InvalidOperationException("任务缺少用户 ID 列表");
    }

    private static BotTelegramService.BotAdminRights ConvertRights(BotSetAdminsRightsPayload rights)
    {
        return new BotTelegramService.BotAdminRights(
            ManageChat: rights.ManageChat,
            ChangeInfo: rights.ChangeInfo,
            PostMessages: rights.PostMessages,
            EditMessages: rights.EditMessages,
            DeleteMessages: rights.DeleteMessages,
            InviteUsers: rights.InviteUsers,
            RestrictMembers: rights.RestrictMembers,
            PinMessages: rights.PinMessages,
            PromoteMembers: rights.PromoteMembers);
    }

    private static string NormalizeReason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? "失败" : text;
    }

    private static string NormalizeChannelTitle(BotTaskChannelItem channel)
    {
        var title = (channel.Title ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(title) ? channel.TelegramId.ToString() : title;
    }

    private static string SerializeIndented(BotSetAdminsTaskConfig config)
    {
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
