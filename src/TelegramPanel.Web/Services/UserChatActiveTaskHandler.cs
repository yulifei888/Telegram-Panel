using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class UserChatActiveTaskHandler : IModuleTaskHandler
{
    private const int MaxFailureLines = 100;

    public string TaskType => BatchTaskTypes.UserChatActive;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<UserChatActiveTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var accountManagement = host.Services.GetRequiredService<AccountManagementService>();
        var accountTools = host.Services.GetRequiredService<AccountTelegramToolsService>();

        var config = DeserializeConfig(host.Config);
        ValidateAndNormalizeConfig(config);

        var allAccounts = (await accountManagement.GetAllAccountsAsync())
            .Where(x => x.IsActive && x.UserId > 0 && x.Category?.ExcludeFromOperations != true)
            .Where(x => x.CategoryId == config.CategoryId)
            .OrderBy(x => x.Id)
            .ToList();

        if (allAccounts.Count == 0)
            throw new InvalidOperationException("所选分类下没有可用执行账号");

        var accountSlots = new List<AccountSlot>();
        foreach (var account in allAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
            {
                config.Canceled = true;
                await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
                return;
            }

            var slot = new AccountSlot(account);
            foreach (var rawTarget in config.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
                    return;
                }

                var resolved = await accountTools.ResolveChatTargetAsync(account.Id, rawTarget, cancellationToken);
                if (resolved.Success && resolved.Target != null)
                {
                    slot.Targets.Add(new TargetSlot(rawTarget, resolved.Target));
                    continue;
                }

                AddFailure(config, account, rawTarget, NormalizeReason(resolved.Error));
            }

            if (slot.Targets.Count > 0)
                accountSlots.Add(slot);
        }

        if (accountSlots.Count == 0)
        {
            config.Error = "没有可用的账号-目标组合（请确认账号已加入目标群组/频道）";
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
            throw new InvalidOperationException(config.Error);
        }

        await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));

        var completed = 0;
        var failed = 0;
        var accountQueueIndex = 0;
        var messageQueueIndex = 0;
        var targetQueueIndexByAccountId = new Dictionary<int, int>();
        var lastProgressPersistAt = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    break;
                }

                if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                    break;

                var accountIdx = SelectIndex(config.AccountMode, accountSlots.Count, ref accountQueueIndex);
                var accountSlot = accountSlots[accountIdx];

                if (!targetQueueIndexByAccountId.ContainsKey(accountSlot.Account.Id))
                    targetQueueIndexByAccountId[accountSlot.Account.Id] = 0;

                var targetQueueIndex = targetQueueIndexByAccountId[accountSlot.Account.Id];
                var targetIdx = SelectIndex(config.TargetMode, accountSlot.Targets.Count, ref targetQueueIndex);
                targetQueueIndexByAccountId[accountSlot.Account.Id] = targetQueueIndex;
                var targetSlot = accountSlot.Targets[targetIdx];

                var messageIdx = SelectIndex(config.MessageMode, config.Dictionary.Count, ref messageQueueIndex);
                var text = config.Dictionary[messageIdx];

                var send = await accountTools.SendMessageToResolvedChatAsync(
                    accountSlot.Account.Id,
                    targetSlot.Resolved,
                    text,
                    cancellationToken);

                completed++;
                var hadFailureThisRound = false;

                if (!send.Success)
                {
                    failed++;
                    hadFailureThisRound = true;
                    AddFailure(config, accountSlot.Account, targetSlot.RawTarget, NormalizeReason(send.Error));

                    if (LooksLikePeerInvalid(send.Error))
                    {
                        var refresh = await accountTools.ResolveChatTargetAsync(
                            accountSlot.Account.Id,
                            targetSlot.RawTarget,
                            cancellationToken);

                        if (refresh.Success && refresh.Target != null)
                            targetSlot.Resolved = refresh.Target;
                    }

                    await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
                }

                if (ShouldPersistProgress(completed, hadFailureThisRound, lastProgressPersistAt))
                {
                    await host.UpdateProgressAsync(completed, failed, cancellationToken);
                    lastProgressPersistAt = DateTime.UtcNow;
                }

                if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                    break;

                var delayMs = NextDelayMilliseconds(config.DelayMinMs, config.DelayMaxMs);
                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UserChatActive task failed (taskId={TaskId})", host.TaskId);
            config.Error = ex.Message;
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
            throw;
        }

        await host.UpdateProgressAsync(completed, failed, cancellationToken);
        if (config.Canceled)
        {
            await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
            return;
        }

        config.Error = null;
        await taskManagement.UpdateTaskConfigAsync(host.TaskId, SerializeIndented(config));
    }

    private static UserChatActiveTaskConfig DeserializeConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        try
        {
            return JsonSerializer.Deserialize<UserChatActiveTaskConfig>(raw)
                   ?? throw new InvalidOperationException("任务 Config JSON 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }
    }

    private static void ValidateAndNormalizeConfig(UserChatActiveTaskConfig config)
    {
        if (config.CategoryId <= 0)
            throw new InvalidOperationException("任务缺少账号分类");

        config.Targets = config.Targets
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.Dictionary = config.Dictionary
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (config.Targets.Count == 0)
            throw new InvalidOperationException("任务缺少目标群组/频道");

        if (config.Dictionary.Count == 0)
            throw new InvalidOperationException("任务缺少词典消息");

        if (config.DelayMinMs < 0) config.DelayMinMs = 0;
        if (config.DelayMaxMs < 0) config.DelayMaxMs = 0;
        if (config.DelayMinMs > 600000) config.DelayMinMs = 600000;
        if (config.DelayMaxMs > 600000) config.DelayMaxMs = 600000;
        if (config.DelayMaxMs < config.DelayMinMs) config.DelayMaxMs = config.DelayMinMs;

        if (config.MaxMessages < 0) config.MaxMessages = 0;

        config.AccountMode = NormalizeMode(config.AccountMode);
        config.TargetMode = NormalizeMode(config.TargetMode);
        config.MessageMode = NormalizeMode(config.MessageMode);

        config.RecentFailures ??= new List<UserChatActiveTaskRuntimeFailure>();
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals((mode ?? string.Empty).Trim(), UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase)
            ? UserChatActiveTaskModes.Queue
            : UserChatActiveTaskModes.Random;
    }

    private static int SelectIndex(string mode, int count, ref int queueIndex)
    {
        if (count <= 1)
            return 0;

        if (string.Equals(mode, UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            var idx = queueIndex % count;
            queueIndex = (queueIndex + 1) % int.MaxValue;
            return idx;
        }

        return Random.Shared.Next(0, count);
    }

    private static int NextDelayMilliseconds(int minMs, int maxMs)
    {
        if (minMs <= 0 && maxMs <= 0)
            return 0;

        if (maxMs <= minMs)
            return minMs;

        return Random.Shared.Next(minMs, maxMs + 1);
    }

    private static bool ShouldPersistProgress(int completed, bool hadFailureThisRound, DateTime lastPersistAt)
    {
        if (completed <= 1)
            return true;

        if (hadFailureThisRound)
            return true;

        if (completed % 5 == 0)
            return true;

        return (DateTime.UtcNow - lastPersistAt) >= TimeSpan.FromSeconds(10);
    }

    private static void AddFailure(UserChatActiveTaskConfig config, Account account, string rawTarget, string reason)
    {
        config.RecentFailures ??= new List<UserChatActiveTaskRuntimeFailure>();
        config.RecentFailures.Add(new UserChatActiveTaskRuntimeFailure
        {
            TimeUtc = DateTime.UtcNow,
            AccountId = account.Id,
            Account = BuildAccountDisplayName(account),
            Target = (rawTarget ?? string.Empty).Trim(),
            Reason = reason
        });

        if (config.RecentFailures.Count > MaxFailureLines)
            config.RecentFailures.RemoveRange(0, config.RecentFailures.Count - MaxFailureLines);
    }

    private static string BuildAccountDisplayName(Account account)
    {
        var nickname = string.IsNullOrWhiteSpace(account.Nickname) ? "" : $" ({account.Nickname.Trim()})";
        return $"{account.DisplayPhone}#{account.Id}{nickname}";
    }

    private static bool LooksLikePeerInvalid(string? error)
    {
        var text = (error ?? string.Empty).ToUpperInvariant();
        return text.Contains("PEER_ID_INVALID", StringComparison.Ordinal)
               || text.Contains("CHAT_ID_INVALID", StringComparison.Ordinal)
               || text.Contains("CHANNEL_INVALID", StringComparison.Ordinal)
               || text.Contains("USERNAME_INVALID", StringComparison.Ordinal)
               || text.Contains("USERNAME_NOT_OCCUPIED", StringComparison.Ordinal);
    }

    private static string NormalizeReason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Length == 0 ? "失败" : text;
    }

    private static string SerializeIndented(UserChatActiveTaskConfig config)
    {
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed class AccountSlot
    {
        public AccountSlot(Account account)
        {
            Account = account;
        }

        public Account Account { get; }
        public List<TargetSlot> Targets { get; } = new();
    }

    private sealed class TargetSlot
    {
        public TargetSlot(string rawTarget, AccountTelegramToolsService.ResolvedChatTarget resolved)
        {
            RawTarget = rawTarget;
            Resolved = resolved;
        }

        public string RawTarget { get; }
        public AccountTelegramToolsService.ResolvedChatTarget Resolved { get; set; }
    }
}
