using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        var templateRendering = host.Services.GetRequiredService<TemplateRenderingService>();
        var aiVerification = host.Services.GetRequiredService<UserChatActiveAiVerificationService>();
        var aiOptions = host.Services.GetRequiredService<IOptionsMonitor<AiOpenAiOptions>>();

        var config = DeserializeConfig(host.Config);
        ValidateAndNormalizeConfig(config);
        config.Canceled = false;
        config.Error = null;
        var configGate = new SemaphoreSlim(1, 1);

        if (config.EnableAiVerification)
        {
            var settings = aiOptions.CurrentValue.ToSnapshot();
            if (!settings.TryValidateForTask(config.AiModel, out var aiError))
                throw new InvalidOperationException($"AI 验证已启用，但全局 AI 配置无效：{aiError}");
        }

        var selectedCategoryIds = NormalizeSelectedCategoryIds(config).ToHashSet();
        var allAccounts = (await accountManagement.GetAllAccountsAsync())
            .Where(x => x.IsActive && x.UserId > 0 && x.Category?.ExcludeFromOperations != true)
            .Where(x => x.CategoryId.HasValue && selectedCategoryIds.Contains(x.CategoryId.Value))
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
                await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
                return;
            }

            var slot = new AccountSlot(account);
            foreach (var rawTarget in config.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
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
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
            throw new InvalidOperationException(config.Error);
        }

        await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);

        var progress = new TaskProgressCounter();
        var verificationTasks = new ConcurrentDictionary<Guid, Task>();
        using var verificationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accountQueueIndex = 0;
        var messageQueueIndex = 0;
        var targetQueueIndexByAccountId = new Dictionary<int, int>();
        var lastProgressPersistAt = DateTime.UtcNow;

        try
        {
            async Task<bool> DelayUntilNextSendAsync(Stopwatch timer, int intervalMs)
            {
                if (intervalMs <= 0)
                    return true;

                var remaining = intervalMs - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0)
                    return true;

                return await DelayWithPauseCheckAsync(host, remaining, cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }

                if (config.MaxMessages > 0 && progress.Completed >= config.MaxMessages)
                    break;

                var intervalMs = NextDelayMilliseconds(config.DelayMinMs, config.DelayMaxMs);
                var loopTimer = Stopwatch.StartNew();

                var accountIdx = SelectIndex(config.AccountMode, accountSlots.Count, ref accountQueueIndex);
                var accountSlot = accountSlots[accountIdx];

                if (!targetQueueIndexByAccountId.ContainsKey(accountSlot.Account.Id))
                    targetQueueIndexByAccountId[accountSlot.Account.Id] = 0;

                var targetQueueIndex = targetQueueIndexByAccountId[accountSlot.Account.Id];
                var targetIdx = SelectIndex(config.TargetMode, accountSlot.Targets.Count, ref targetQueueIndex);
                targetQueueIndexByAccountId[accountSlot.Account.Id] = targetQueueIndex;
                var targetSlot = accountSlot.Targets[targetIdx];

                var messageIdx = SelectIndex(config.MessageMode, config.Dictionary.Count, ref messageQueueIndex);
                var textTemplate = config.Dictionary[messageIdx];

                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }

                string text;
                try
                {
                    text = (await templateRendering.RenderTextTemplateAsync(textTemplate, cancellationToken)).Trim();
                }
                catch (Exception ex)
                {
                    var completed = Interlocked.Increment(ref progress.Completed);
                    Interlocked.Increment(ref progress.Failed);
                    var hadTemplateFailure = true;
                    await AddFailureAndPersistAsync(
                        taskManagement,
                        host.TaskId,
                        config,
                        accountSlot.Account,
                        targetSlot.RawTarget,
                        $"词典模板解析失败：{ex.Message}",
                        configGate,
                        cancellationToken);

                    if (ShouldPersistProgress(completed, hadTemplateFailure, lastProgressPersistAt))
                    {
                        await host.UpdateProgressAsync(completed, progress.Failed, cancellationToken);
                        lastProgressPersistAt = DateTime.UtcNow;
                    }

                    if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                        break;

                    if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                    {
                        config.Canceled = true;
                        verificationTokenSource.Cancel();
                        break;
                    }

                    continue;
                }

                if (text.Length == 0)
                {
                    var completed = Interlocked.Increment(ref progress.Completed);
                    Interlocked.Increment(ref progress.Failed);
                    var hadEmptyMessageFailure = true;
                    await AddFailureAndPersistAsync(
                        taskManagement,
                        host.TaskId,
                        config,
                        accountSlot.Account,
                        targetSlot.RawTarget,
                        "词典模板解析结果为空，无法发送",
                        configGate,
                        cancellationToken);

                    if (ShouldPersistProgress(completed, hadEmptyMessageFailure, lastProgressPersistAt))
                    {
                        await host.UpdateProgressAsync(completed, progress.Failed, cancellationToken);
                        lastProgressPersistAt = DateTime.UtcNow;
                    }

                    if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                        break;

                    if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                    {
                        config.Canceled = true;
                        verificationTokenSource.Cancel();
                        break;
                    }

                    continue;
                }

                var send = await accountTools.SendMessageToResolvedChatAsync(
                    accountSlot.Account.Id,
                    targetSlot.Resolved,
                    text,
                    cancellationToken: cancellationToken);

                var sendCompleted = Interlocked.Increment(ref progress.Completed);
                var hadFailureThisRound = false;

                if (!send.Success)
                {
                    Interlocked.Increment(ref progress.Failed);
                    hadFailureThisRound = true;
                    await AddFailureAndPersistAsync(
                        taskManagement,
                        host.TaskId,
                        config,
                        accountSlot.Account,
                        targetSlot.RawTarget,
                        NormalizeReason(send.Error),
                        configGate,
                        cancellationToken);

                    if (LooksLikePeerInvalid(send.Error))
                    {
                        var refresh = await accountTools.ResolveChatTargetAsync(
                            accountSlot.Account.Id,
                            targetSlot.RawTarget,
                            cancellationToken);

                        if (refresh.Success && refresh.Target != null)
                            targetSlot.Resolved = refresh.Target;
                    }
                }
                else if (config.EnableAiVerification)
                {
                    if (!send.MessageId.HasValue || send.MessageId.Value <= 0)
                    {
                        Interlocked.Increment(ref progress.Failed);
                        hadFailureThisRound = true;
                        await AddFailureAndPersistAsync(
                            taskManagement,
                            host.TaskId,
                            config,
                            accountSlot.Account,
                            targetSlot.RawTarget,
                            "消息已发送，但未获取到消息 ID，无法执行 AI 验证",
                            configGate,
                            cancellationToken);
                    }
                    else
                    {
                        var verificationTaskId = Guid.NewGuid();
                        var verificationTask = RunVerificationAsync(
                            aiVerification,
                            accountSlot.Account,
                            targetSlot.Resolved,
                            targetSlot.RawTarget,
                            send.MessageId.Value,
                            config,
                            taskManagement,
                            host,
                            progress,
                            configGate,
                            logger,
                            verificationTokenSource.Token);

                        verificationTasks[verificationTaskId] = verificationTask;
                        _ = verificationTask.ContinueWith(
                            _ => verificationTasks.TryRemove(verificationTaskId, out _),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }

                if (ShouldPersistProgress(sendCompleted, hadFailureThisRound, lastProgressPersistAt))
                {
                    await host.UpdateProgressAsync(sendCompleted, progress.Failed, cancellationToken);
                    lastProgressPersistAt = DateTime.UtcNow;
                }

                if (config.MaxMessages > 0 && sendCompleted >= config.MaxMessages)
                    break;

                if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }
            }

            var pendingVerifications = verificationTasks.Values.ToArray();
            if (pendingVerifications.Length > 0)
                await Task.WhenAll(pendingVerifications);
        }
        catch (Exception ex)
        {
            verificationTokenSource.Cancel();
            var pendingVerifications = verificationTasks.Values.ToArray();
            if (pendingVerifications.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingVerifications);
                }
                catch
                {
                    // 忽略验证任务的二次异常，避免覆盖主异常。
                }
            }

            logger.LogWarning(ex, "UserChatActive task failed (taskId={TaskId})", host.TaskId);
            config.Error = ex.Message;
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
            throw;
        }

        await host.UpdateProgressAsync(progress.Completed, progress.Failed, cancellationToken);
        if (config.Canceled)
        {
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
            return;
        }

        config.Error = null;
        await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
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
        config.CategoryIds = NormalizeSelectedCategoryIds(config);
        if (config.CategoryIds.Count == 0)
            throw new InvalidOperationException("任务缺少账号分类");

        config.CategoryId = config.CategoryIds[0];
        config.CategoryNames = NormalizeSelectedCategoryNames(config);
        config.CategoryName = config.CategoryNames.FirstOrDefault() ?? config.CategoryName;

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
        if (config.VerificationTimeoutSeconds < 3) config.VerificationTimeoutSeconds = 15;
        if (config.VerificationTimeoutSeconds > 300) config.VerificationTimeoutSeconds = 300;

        config.AiModel = AiOpenAiSettingsSnapshot.NormalizeModel(config.AiModel);

        config.AccountMode = NormalizeMode(config.AccountMode);
        config.TargetMode = NormalizeMode(config.TargetMode);
        config.MessageMode = NormalizeMode(config.MessageMode);

        config.RecentFailures ??= new List<UserChatActiveTaskRuntimeFailure>();
    }

    private static List<int> NormalizeSelectedCategoryIds(UserChatActiveTaskConfig config)
    {
        var ids = (config.CategoryIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0 && config.CategoryId > 0)
            ids.Add(config.CategoryId);

        return ids;
    }

    private static List<string> NormalizeSelectedCategoryNames(UserChatActiveTaskConfig config)
    {
        var names = (config.CategoryNames ?? new List<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fallbackName = (config.CategoryName ?? string.Empty).Trim();
        if (names.Count == 0 && fallbackName.Length > 0)
            names.Add(fallbackName);

        return names;
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

    private static async Task<bool> DelayWithPauseCheckAsync(
        IModuleTaskExecutionHost host,
        int delayMs,
        CancellationToken cancellationToken)
    {
        if (delayMs <= 0)
            return true;

        var remaining = delayMs;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
                return false;

            var chunk = Math.Min(remaining, 1000);
            await Task.Delay(chunk, cancellationToken);
            remaining -= chunk;
        }

        return true;
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

    private static async Task PersistConfigAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserChatActiveTaskConfig config,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await taskManagement.UpdateTaskConfigAsync(taskId, SerializeIndented(config));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task AddFailureAndPersistAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserChatActiveTaskConfig config,
        Account account,
        string rawTarget,
        string reason,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            AddFailure(config, account, rawTarget, reason);
            await taskManagement.UpdateTaskConfigAsync(taskId, SerializeIndented(config));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task RunVerificationAsync(
        UserChatActiveAiVerificationService aiVerification,
        Account account,
        AccountTelegramToolsService.ResolvedChatTarget target,
        string rawTarget,
        int sentMessageId,
        UserChatActiveTaskConfig config,
        BatchTaskManagementService taskManagement,
        IModuleTaskExecutionHost host,
        TaskProgressCounter progress,
        SemaphoreSlim configGate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var timeoutSeconds = Math.Clamp(config.VerificationTimeoutSeconds, 3, 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));

        try
        {
            var verification = await aiVerification.TryHandleAsync(
                account,
                target,
                sentMessageId,
                config,
                timeoutCts.Token);

            if (!verification.Success)
            {
                var failed = Interlocked.Increment(ref progress.Failed);
                await AddFailureAndPersistAsync(
                    taskManagement,
                    host.TaskId,
                    config,
                    account,
                    rawTarget,
                    NormalizeReason(verification.Error),
                    configGate,
                    CancellationToken.None);
                await host.UpdateProgressAsync(progress.Completed, failed, CancellationToken.None);
            }
            else
            {
                logger.LogInformation(
                    "UserChatActive AI verification completed: taskId={TaskId}, accountId={AccountId}, target={Target}, action={Action}",
                    host.TaskId,
                    account.Id,
                    rawTarget,
                    verification.ActionSummary ?? "(none)");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 任务被取消时忽略验证
        }
        catch (OperationCanceledException)
        {
            var failed = Interlocked.Increment(ref progress.Failed);
            await AddFailureAndPersistAsync(
                taskManagement,
                host.TaskId,
                config,
                account,
                rawTarget,
                "验证处理超时",
                configGate,
                CancellationToken.None);
            await host.UpdateProgressAsync(progress.Completed, failed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var failed = Interlocked.Increment(ref progress.Failed);
            await AddFailureAndPersistAsync(
                taskManagement,
                host.TaskId,
                config,
                account,
                rawTarget,
                $"验证处理异常：{ex.Message}",
                configGate,
                CancellationToken.None);
            await host.UpdateProgressAsync(progress.Completed, failed, CancellationToken.None);
        }
    }

    private sealed class TaskProgressCounter
    {
        public int Completed;
        public int Failed;
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
