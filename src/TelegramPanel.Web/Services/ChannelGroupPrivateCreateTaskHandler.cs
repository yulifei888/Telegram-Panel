using System.Text.Json;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 自动创建私密频道/群组任务处理器。
/// </summary>
public sealed class ChannelGroupPrivateCreateTaskHandler : IModuleTaskHandler
{
    public string TaskType => BatchTaskTypes.ChannelGroupPrivateCreate;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var config = Deserialize(host.Config);
        Validate(config);

        var accountManagement = host.Services.GetRequiredService<AccountManagementService>();
        var channelManagement = host.Services.GetRequiredService<ChannelManagementService>();
        var groupManagement = host.Services.GetRequiredService<GroupManagementService>();
        var channelService = host.Services.GetRequiredService<IChannelService>();
        var groupService = host.Services.GetRequiredService<IGroupService>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var templateRendering = host.Services.GetRequiredService<TemplateRenderingService>();
        var assetStorage = host.Services.GetRequiredService<ImageAssetStorageService>();

        var selectedCategoryIds = config.CategoryIds.Where(x => x > 0).ToHashSet();
        var accounts = (await accountManagement.GetActiveAccountsAsync())
            .Where(x => x.UserId > 0)
            .Where(x => x.CategoryId.HasValue && selectedCategoryIds.Contains(x.CategoryId.Value))
            .Where(x => x.Category?.ExcludeFromOperations != true)
            .OrderBy(x => x.Id)
            .ToList();
        if (accounts.Count == 0)
            throw new InvalidOperationException("所选账号分类下没有可用账号");

        var created = 0;
        var failed = 0;
        var systemCreatedCountByAccount = string.Equals(config.CreateType, ChannelGroupAutomationTaskObjectTypes.Channel, StringComparison.OrdinalIgnoreCase)
            ? (await channelManagement.GetAllChannelsAsync())
                .Where(x => x.CreatorAccountId.HasValue && x.SystemCreatedAtUtc.HasValue)
                .GroupBy(x => x.CreatorAccountId!.Value)
                .ToDictionary(x => x.Key, x => x.Count())
            : (await groupManagement.GetAllGroupsAsync())
                .Where(x => x.CreatorAccountId.HasValue && x.SystemCreatedAtUtc.HasValue)
                .GroupBy(x => x.CreatorAccountId!.Value)
                .ToDictionary(x => x.Key, x => x.Count());

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
                return;

            var createdCount = systemCreatedCountByAccount.TryGetValue(account.Id, out var current) ? current : 0;
            var processedForAccount = 0;
            while (processedForAccount < config.PerAccountBatchSize && createdCount < config.SystemCreatedLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                    return;

                try
                {
                    var title = (await templateRendering.RenderTextTemplateAsync(config.TitleTemplate, cancellationToken)).Trim();
                    if (title.Length == 0)
                        throw new InvalidOperationException("标题模板解析结果为空");

                    if (string.Equals(config.CreateType, ChannelGroupAutomationTaskObjectTypes.Channel, StringComparison.OrdinalIgnoreCase))
                    {
                        var info = await channelService.CreateChannelAsync(account.Id, title, string.Empty, isPublic: false);
                        var now = DateTime.UtcNow;
                        var channel = new Channel
                        {
                            TelegramId = info.TelegramId,
                            AccessHash = info.AccessHash,
                            Title = title,
                            Username = null,
                            IsBroadcast = true,
                            MemberCount = info.MemberCount,
                            About = string.Empty,
                            CreatorAccountId = account.Id,
                            GroupId = config.ChannelGroupId,
                            CreatedAt = info.CreatedAt ?? now,
                            SystemCreatedAtUtc = now,
                            SyncedAt = now
                        };
                        var saved = await channelManagement.CreateOrUpdateChannelAsync(channel);
                        await channelManagement.UpsertAccountChannelAsync(account.Id, saved.Id, true, true, now);
                        await ApplyAvatarIfNeededAsync(config, saved.TelegramId, isChannel: true, account.Id, templateRendering, assetStorage, channelService, groupService, cancellationToken);
                    }
                    else
                    {
                        var info = await groupService.CreateGroupAsync(account.Id, title, string.Empty, isPublic: false, username: null);
                        var now = DateTime.UtcNow;
                        var group = new Group
                        {
                            TelegramId = info.TelegramId,
                            AccessHash = info.AccessHash,
                            Title = title,
                            Username = null,
                            MemberCount = Math.Max(info.MemberCount, 1),
                            About = string.Empty,
                            CreatorAccountId = account.Id,
                            CategoryId = config.GroupCategoryId,
                            CreatedAt = info.CreatedAt ?? now,
                            SystemCreatedAtUtc = now,
                            SyncedAt = now
                        };
                        var saved = await groupManagement.CreateOrUpdateGroupAsync(group);
                        await groupManagement.UpsertAccountGroupAsync(account.Id, saved.Id, true, true, now);
                        await ApplyAvatarIfNeededAsync(config, saved.TelegramId, isChannel: false, account.Id, templateRendering, assetStorage, channelService, groupService, cancellationToken);
                    }

                    processedForAccount++;
                    createdCount++;
                    systemCreatedCountByAccount[account.Id] = createdCount;
                    created++;
                    await host.UpdateProgressAsync(created, failed, cancellationToken);
                }
                catch
                {
                    processedForAccount++;
                    created++;
                    failed++;
                    await host.UpdateProgressAsync(created, failed, cancellationToken);
                }

                if (processedForAccount < config.PerAccountBatchSize && createdCount < config.SystemCreatedLimit)
                {
                    var continued = await ChannelGroupTaskDelayHelper.DelayAsync(
                        host,
                        config.MinDelaySeconds,
                        config.MaxDelaySeconds,
                        config.JitterPercent,
                        cancellationToken);
                    if (!continued)
                        return;
                }
            }
        }

        await taskManagement.UpdateTaskDraftAsync(host.TaskId, created, host.Config);
    }

    private static async Task ApplyAvatarIfNeededAsync(
        ChannelGroupPrivateCreateTaskConfig config,
        long telegramId,
        bool isChannel,
        int accountId,
        TemplateRenderingService templateRendering,
        ImageAssetStorageService assetStorage,
        IChannelService channelService,
        IGroupService groupService,
        CancellationToken cancellationToken)
    {
        if (string.Equals(config.AvatarSource, ChannelGroupAutomationAvatarSourceModes.None, StringComparison.OrdinalIgnoreCase))
            return;

        StoredImageAssetInfo? asset = null;
        if (string.Equals(config.AvatarSource, ChannelGroupAutomationAvatarSourceModes.Fixed, StringComparison.OrdinalIgnoreCase))
        {
            var path = (config.FixedAvatarAssetPath ?? string.Empty).Trim();
            if (path.Length == 0)
                throw new InvalidOperationException("固定头像路径为空");
            asset = new StoredImageAssetInfo(path, Path.GetFileName(path));
        }
        else if (string.Equals(config.AvatarSource, ChannelGroupAutomationAvatarSourceModes.Dictionary, StringComparison.OrdinalIgnoreCase))
        {
            asset = await templateRendering.ResolveImageTemplateAsync(config.AvatarDictionaryToken ?? string.Empty, cancellationToken);
        }

        if (asset == null)
            return;

        await using var stream = await assetStorage.OpenReadAsync(asset.AssetPath, cancellationToken);
        if (isChannel)
            await channelService.SetChannelPhotoAsync(accountId, telegramId, stream, asset.FileName, cancellationToken);
        else
            await groupService.SetGroupPhotoAsync(accountId, telegramId, stream, asset.FileName, cancellationToken);
    }

    private static ChannelGroupPrivateCreateTaskConfig Deserialize(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidOperationException("任务配置为空");

        return JsonSerializer.Deserialize<ChannelGroupPrivateCreateTaskConfig>(config)
               ?? throw new InvalidOperationException("任务配置解析失败");
    }

    private static void Validate(ChannelGroupPrivateCreateTaskConfig config)
    {
        config.CategoryIds = (config.CategoryIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        if (config.CategoryIds.Count == 0)
            throw new InvalidOperationException("请至少选择一个账号分类");

        config.CreateType = string.Equals(config.CreateType, ChannelGroupAutomationTaskObjectTypes.Group, StringComparison.OrdinalIgnoreCase)
            ? ChannelGroupAutomationTaskObjectTypes.Group
            : ChannelGroupAutomationTaskObjectTypes.Channel;

        config.TitleTemplate = (config.TitleTemplate ?? string.Empty).Trim();
        if (config.TitleTemplate.Length == 0)
            throw new InvalidOperationException("标题模板不能为空");

        config.SystemCreatedLimit = Math.Max(1, config.SystemCreatedLimit);
        config.PerAccountBatchSize = Math.Max(1, config.PerAccountBatchSize);
        config.MinDelaySeconds = ChannelGroupTaskDelayHelper.NormalizeMinDelay(config.MinDelaySeconds);
        config.MaxDelaySeconds = ChannelGroupTaskDelayHelper.NormalizeMaxDelay(config.MinDelaySeconds, config.MaxDelaySeconds);
        config.JitterPercent = ChannelGroupTaskDelayHelper.NormalizeJitterPercent(config.JitterPercent);
        config.AvatarSource = NormalizeAvatarSource(config.AvatarSource);
    }

    private static string NormalizeAvatarSource(string? value)
    {
        return string.Equals((value ?? string.Empty).Trim(), ChannelGroupAutomationAvatarSourceModes.Fixed, StringComparison.OrdinalIgnoreCase)
            ? ChannelGroupAutomationAvatarSourceModes.Fixed
            : string.Equals((value ?? string.Empty).Trim(), ChannelGroupAutomationAvatarSourceModes.Dictionary, StringComparison.OrdinalIgnoreCase)
                ? ChannelGroupAutomationAvatarSourceModes.Dictionary
                : ChannelGroupAutomationAvatarSourceModes.None;
    }
}
