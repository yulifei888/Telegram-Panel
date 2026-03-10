using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class BotChannelSetAdminsByAccountTaskHandler : IModuleTaskHandler
{
    public string TaskType => BatchTaskTypes.BotChannelSetAdminsByAccount;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<BotChannelSetAdminsByAccountTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var accountManagement = host.Services.GetRequiredService<AccountManagementService>();
        var channelService = host.Services.GetRequiredService<IChannelService>();
        var botTelegram = host.Services.GetRequiredService<BotTelegramService>();
        var accountTools = host.Services.GetRequiredService<AccountTelegramToolsService>();

        var config = DeserializeConfig(host.Config);
        ValidateConfig(config);

        var channels = config.Channels
            .Where(x => x != null && x.TelegramId != 0)
            .GroupBy(x => x.TelegramId)
            .Select(x => x.First())
            .ToList();
        var usernames = config.Usernames
            .Select(x => (x ?? string.Empty).Trim().TrimStart('@'))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channels.Count == 0 || usernames.Count == 0)
            throw new InvalidOperationException("任务缺少有效的频道或用户名");

        var allAccounts = (await accountManagement.GetAllAccountsAsync())
            .Where(x => x.IsActive && x.Category?.ExcludeFromOperations != true && x.UserId > 0)
            .ToList();

        var accountsById = allAccounts.ToDictionary(x => x.Id);
        var accountsByUserId = new Dictionary<long, Account>();
        foreach (var account in allAccounts)
        {
            if (!accountsByUserId.ContainsKey(account.UserId))
                accountsByUserId[account.UserId] = account;
        }

        var channelAdmins = new Dictionary<long, List<BotTelegramService.BotChatAdminInfo>>();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
            {
                config.Canceled = true;
                await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
                return;
            }

            try
            {
                channelAdmins[channel.TelegramId] = await botTelegram.GetChatAdminsAsync(
                    config.BotId,
                    channel.TelegramId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GetChatAdmins failed for bot {BotId} chat {ChatId}", config.BotId, channel.TelegramId);
                channelAdmins[channel.TelegramId] = new List<BotTelegramService.BotChatAdminInfo>();
            }
        }

        var completed = 0;
        var failed = 0;
        var failures = new List<BotAdminTaskFailureItem>();
        var joinTried = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    break;
                }

                var (executorId, reason) = ResolveExecutorAccountId(
                    channel,
                    config.SelectedAccountId,
                    channelAdmins,
                    accountsById,
                    accountsByUserId);

                if (!executorId.HasValue)
                {
                    foreach (var _ in usernames)
                    {
                        completed++;
                        failed++;
                    }

                    failures.Add(new BotAdminTaskFailureItem
                    {
                        ChannelTelegramId = channel.TelegramId,
                        ChannelTitle = NormalizeChannelTitle(channel),
                        Reason = NormalizeReason(reason ?? "无可用执行账号")
                    });

                    await host.UpdateProgressAsync(completed, failed, cancellationToken);
                    continue;
                }

                foreach (var username in usernames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await host.IsStillRunningAsync(cancellationToken))
                    {
                        config.Canceled = true;
                        break;
                    }

                    try
                    {
                        var ok = await channelService.SetAdminAsync(
                            executorId.Value,
                            channel.TelegramId,
                            username,
                            (AdminRights)config.Rights,
                            config.AdminTitle);

                        if (!ok)
                        {
                            failed++;
                            failures.Add(new BotAdminTaskFailureItem
                            {
                                ChannelTelegramId = channel.TelegramId,
                                ChannelTitle = NormalizeChannelTitle(channel),
                                Username = username,
                                Reason = "失败"
                            });
                        }
                    }
                    catch (Exception ex)
                        when (!joinTried.Contains($"{executorId.Value}:{channel.TelegramId}")
                              && BotChannelJoinRetryHelper.LooksLikeChannelNotFound(ex.Message))
                    {
                        var key = $"{executorId.Value}:{channel.TelegramId}";
                        joinTried.Add(key);

                        var joinFailures = new List<string>();
                        var joined = await BotChannelJoinRetryHelper.TryJoinChannelAsync(
                            botTelegram,
                            accountTools,
                            config.BotId,
                            executorId.Value,
                            new BotChannel
                            {
                                TelegramId = channel.TelegramId,
                                Title = NormalizeChannelTitle(channel)
                            },
                            joinFailures,
                            cancellationToken);

                        foreach (var joinFailure in joinFailures)
                        {
                            failures.Add(new BotAdminTaskFailureItem
                            {
                                ChannelTelegramId = channel.TelegramId,
                                ChannelTitle = NormalizeChannelTitle(channel),
                                Username = username,
                                Reason = TrimJoinFailurePrefix(joinFailure, NormalizeChannelTitle(channel))
                            });
                        }

                        if (!joined)
                        {
                            failed++;
                            if (joinFailures.Count == 0)
                            {
                                failures.Add(new BotAdminTaskFailureItem
                                {
                                    ChannelTelegramId = channel.TelegramId,
                                    ChannelTitle = NormalizeChannelTitle(channel),
                                    Username = username,
                                    Reason = NormalizeReason(ex.Message)
                                });
                            }
                        }
                        else
                        {
                            try
                            {
                                var ok = await channelService.SetAdminAsync(
                                    executorId.Value,
                                    channel.TelegramId,
                                    username,
                                    (AdminRights)config.Rights,
                                    config.AdminTitle);
                                if (!ok)
                                {
                                    failed++;
                                    failures.Add(new BotAdminTaskFailureItem
                                    {
                                        ChannelTelegramId = channel.TelegramId,
                                        ChannelTitle = NormalizeChannelTitle(channel),
                                        Username = username,
                                        Reason = "失败"
                                    });
                                }
                            }
                            catch (Exception ex2)
                            {
                                failed++;
                                failures.Add(new BotAdminTaskFailureItem
                                {
                                    ChannelTelegramId = channel.TelegramId,
                                    ChannelTitle = NormalizeChannelTitle(channel),
                                    Username = username,
                                    Reason = NormalizeReason(ex2.Message)
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.Add(new BotAdminTaskFailureItem
                        {
                            ChannelTelegramId = channel.TelegramId,
                            ChannelTitle = NormalizeChannelTitle(channel),
                            Username = username,
                            Reason = NormalizeReason(ex.Message)
                        });
                    }
                    finally
                    {
                        completed++;
                    }

                    await host.UpdateProgressAsync(completed, failed, cancellationToken);

                    var wait = config.DelayMs;
                    if (wait < 0) wait = 0;
                    if (wait > 30000) wait = 30000;
                    var jitter = Random.Shared.Next(500, 1000);
                    await Task.Delay(TimeSpan.FromMilliseconds(wait + jitter), cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
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

    private static BotChannelSetAdminsByAccountTaskConfig DeserializeConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        try
        {
            return JsonSerializer.Deserialize<BotChannelSetAdminsByAccountTaskConfig>(raw)
                   ?? throw new InvalidOperationException("任务 Config JSON 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }
    }

    private static void ValidateConfig(BotChannelSetAdminsByAccountTaskConfig config)
    {
        if (config.BotId <= 0)
            throw new InvalidOperationException("任务 BotId 无效");
        if (string.IsNullOrWhiteSpace(config.AdminTitle))
            config.AdminTitle = "Admin";
        config.AdminTitle = config.AdminTitle.Trim();
        if (config.Usernames.Count == 0)
            throw new InvalidOperationException("任务缺少用户名列表");
        if (config.Channels.Count == 0)
            throw new InvalidOperationException("任务缺少频道列表");
    }

    private static (int? ExecutorId, string? Reason) ResolveExecutorAccountId(
        BotTaskChannelItem channel,
        int selectedAccountId,
        IReadOnlyDictionary<long, List<BotTelegramService.BotChatAdminInfo>> channelAdmins,
        IReadOnlyDictionary<int, Account> accountsById,
        IReadOnlyDictionary<long, Account> accountsByUserId)
    {
        if (!channelAdmins.TryGetValue(channel.TelegramId, out var admins) || admins.Count == 0)
            return (null, "无法获取频道管理员列表（请确认 Bot 已加入且为管理员）");

        if (selectedAccountId > 0)
        {
            if (!accountsById.TryGetValue(selectedAccountId, out var selected) || selected.UserId <= 0)
                return (null, "所选执行账号无效");

            var admin = admins.FirstOrDefault(x => x.UserId == selected.UserId);
            if (admin == null)
                return (null, "所选执行账号不是该频道管理员");

            if (!admin.IsCreator && !admin.CanPromoteMembers)
                return (null, "所选执行账号缺少“添加管理员”权限");

            return (selected.Id, null);
        }

        var creator = admins.FirstOrDefault(x => x.IsCreator);
        if (creator != null && accountsByUserId.TryGetValue(creator.UserId, out var creatorAcc))
            return (creatorAcc.Id, null);

        foreach (var admin in admins)
        {
            if (!admin.IsCreator && !admin.CanPromoteMembers)
                continue;

            if (accountsByUserId.TryGetValue(admin.UserId, out var acc))
                return (acc.Id, null);
        }

        return (null, "无可用执行账号（需要该频道管理员且拥有“添加管理员”权限，并且在系统中存在）");
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

    private static string TrimJoinFailurePrefix(string reason, string channelTitle)
    {
        var normalized = NormalizeReason(reason);
        var prefix = $"{channelTitle}：";
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            return normalized[prefix.Length..];
        return normalized;
    }

    private static string SerializeIndented(BotChannelSetAdminsByAccountTaskConfig config)
    {
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
