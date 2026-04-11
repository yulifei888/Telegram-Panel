using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.ExternalApi;

public sealed class ExternalApiKickTaskHandler : IModuleTaskHandler
{
    public string TaskType => BatchTaskTypes.ExternalApiKick;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<ExternalApiKickTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var botManagement = host.Services.GetRequiredService<BotManagementService>();
        var botTelegram = host.Services.GetRequiredService<BotTelegramService>();

        var rawConfig = (host.Config ?? "").Trim();
        if (rawConfig.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        KickTaskLog? input;
        try
        {
            input = JsonSerializer.Deserialize<KickTaskLog>(rawConfig);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }

        if (input == null)
            throw new InvalidOperationException("任务 Config JSON 为空");

        if (input.UserId <= 0)
            throw new InvalidOperationException("任务 user_id 无效");

        input.ChatIds ??= new List<long>();

        var configuredBotId = input.BotId;
        var useAllChats = configuredBotId == 0 ? true : input.UseAllChats;
        var chatIdSet = new HashSet<long>(input.ChatIds.Where(x => x != 0));

        var targets = await ResolveTargetsAsync(botManagement, configuredBotId, useAllChats, chatIdSet, cancellationToken);
        if (targets.TotalChats == 0)
            throw new InvalidOperationException("未匹配到任何可操作的频道/群组");

        var results = new List<KickResultItem>(targets.TotalChats);
        var completed = 0;
        var failed = 0;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;

        try
        {
            foreach (var group in targets.Groups)
            {
                linkedToken.ThrowIfCancellationRequested();

                if (!await host.IsStillRunningAsync(linkedToken))
                {
                    await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(new KickTaskLog
                    {
                        ApiName = input.ApiName ?? "",
                        BotId = configuredBotId,
                        UseAllChats = useAllChats,
                        ChatIds = chatIdSet.OrderBy(x => x).ToList(),
                        UserId = input.UserId,
                        PermanentBan = input.PermanentBan,
                        RequestedAtUtc = input.RequestedAtUtc,
                        Results = results,
                        Canceled = true
                    }));
                    return;
                }

                var baseCompleted = completed;
                var baseFailed = failed;
                var chatById = group.Chats.ToDictionary(c => c.TelegramId);
                var recorded = new HashSet<long>();

                try
                {
                    _ = await botTelegram.BanChatMemberAsync(
                        botId: group.Bot.Id,
                        channelTelegramIds: group.Chats.Select(x => x.TelegramId).ToList(),
                        userId: input.UserId,
                        permanentBan: input.PermanentBan,
                        cancellationToken: linkedToken,
                        perChatCallback: async (chatId, error, okCount, failCount) =>
                        {
                            if (!recorded.Add(chatId))
                                return;

                            var ok = error == null;
                            results.Add(new KickResultItem(
                                ChatId: chatId.ToString(),
                                Title: chatById.TryGetValue(chatId, out var ch) ? ch.Title : chatId.ToString(),
                                Success: ok,
                                Error: error));

                            completed = baseCompleted + okCount;
                            failed = baseFailed + failCount;
                            await host.UpdateProgressAsync(completed, failed, linkedToken);

                            if (!await host.IsStillRunningAsync(linkedToken))
                                linkedCts.Cancel();
                        });
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // 用户主动取消（把任务状态改为非 running）
                    await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(new KickTaskLog
                    {
                        ApiName = input.ApiName ?? "",
                        BotId = configuredBotId,
                        UseAllChats = useAllChats,
                        ChatIds = chatIdSet.OrderBy(x => x).ToList(),
                        UserId = input.UserId,
                        PermanentBan = input.PermanentBan,
                        RequestedAtUtc = input.RequestedAtUtc,
                        Results = results,
                        Canceled = true
                    }));
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ExternalApiKick failed on bot {BotId} (chats={Count})", group.Bot.Id, group.Chats.Count);
                    foreach (var chat in group.Chats)
                    {
                        if (!recorded.Add(chat.TelegramId))
                            continue;

                        results.Add(new KickResultItem(
                            ChatId: chat.TelegramId.ToString(),
                            Title: chat.Title,
                            Success: false,
                            Error: ex.Message));
                        failed++;
                    }

                    await host.UpdateProgressAsync(completed, failed, linkedToken);
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(new KickTaskLog
            {
                ApiName = input.ApiName ?? "",
                BotId = configuredBotId,
                UseAllChats = useAllChats,
                ChatIds = chatIdSet.OrderBy(x => x).ToList(),
                UserId = input.UserId,
                PermanentBan = input.PermanentBan,
                RequestedAtUtc = input.RequestedAtUtc,
                Results = results,
                Canceled = true
            }));
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ExternalApiKick failed (taskId={TaskId})", host.TaskId);
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(new KickTaskLog
            {
                ApiName = input.ApiName ?? "",
                BotId = configuredBotId,
                UseAllChats = useAllChats,
                ChatIds = chatIdSet.OrderBy(x => x).ToList(),
                UserId = input.UserId,
                PermanentBan = input.PermanentBan,
                RequestedAtUtc = input.RequestedAtUtc,
                Results = results,
                Error = ex.Message
            }));
            throw;
        }

        await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(new KickTaskLog
        {
            ApiName = input.ApiName ?? "",
            BotId = configuredBotId,
            UseAllChats = useAllChats,
            ChatIds = chatIdSet.OrderBy(x => x).ToList(),
            UserId = input.UserId,
            PermanentBan = input.PermanentBan,
            RequestedAtUtc = input.RequestedAtUtc,
            Results = results
        }));
    }

    private static async Task<TargetResolution> ResolveTargetsAsync(
        BotManagementService botManagement,
        int configuredBotId,
        bool useAllChats,
        HashSet<long> configuredChatIds,
        CancellationToken cancellationToken)
    {
        var bots = (await botManagement.GetAllBotsAsync())
            .Where(b => b.IsActive)
            .OrderBy(b => b.Id)
            .ToList();

        if (configuredBotId > 0)
            bots = bots.Where(b => b.Id == configuredBotId).ToList();

        var groups = new List<TargetGroup>();
        var totalChats = 0;

        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chats = (await botManagement.GetChatsAsync(bot.Id)).ToList();
            if (!useAllChats && configuredChatIds.Count > 0)
                chats = chats.Where(c => configuredChatIds.Contains(c.TelegramId)).ToList();

            if (chats.Count == 0)
                continue;

            groups.Add(new TargetGroup(bot, chats));
            totalChats += chats.Count;
        }

        return new TargetResolution(groups, totalChats);
    }

    private static string SerializeIndented<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record TargetGroup(Bot Bot, List<BotChannel> Chats);
    private sealed record TargetResolution(List<TargetGroup> Groups, int TotalChats);
}

