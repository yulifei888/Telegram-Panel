using System.Text.Json;
using System.Text.RegularExpressions;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 将私密频道/群组公开化任务处理器。
/// </summary>
public sealed class ChannelGroupPublicizeTaskHandler : IModuleTaskHandler
{
    private static readonly Regex UsernameRegex = new("^[a-z][a-z0-9_]{4,31}$", RegexOptions.Compiled);

    public string TaskType => BatchTaskTypes.ChannelGroupPublicize;

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

        var now = DateTime.UtcNow;
        var processed = 0;
        var failed = 0;
        if (string.Equals(config.TargetType, ChannelGroupAutomationTaskObjectTypes.Channel, StringComparison.OrdinalIgnoreCase))
        {
            var channels = (await channelManagement.GetAllChannelsAsync()).ToList();
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                    return;

                var publicCount = channels.Count(x => x.CreatorAccountId == account.Id && !string.IsNullOrWhiteSpace(x.Username));
                var candidates = channels
                    .Where(x => x.CreatorAccountId == account.Id)
                    .Where(x => string.IsNullOrWhiteSpace(x.Username))
                    .Where(x => x.GroupId == config.ChannelGroupId)
                    .Where(x => x.SystemCreatedAtUtc.HasValue && x.SystemCreatedAtUtc.Value <= now.AddDays(-config.MinSystemCreatedDays))
                    .OrderBy(x => x.SystemCreatedAtUtc)
                    .ThenBy(x => x.Id)
                    .ToList();

                var processedForAccount = 0;
                foreach (var channel in candidates)
                {
                    if (processedForAccount >= config.PerAccountBatchSize || publicCount >= config.MaxPublicCount)
                        break;

                    try
                    {
                        var title = (await templateRendering.RenderTextTemplateAsync(config.TitleTemplate, cancellationToken)).Trim();
                        var about = string.IsNullOrWhiteSpace(config.DescriptionTemplate)
                            ? channel.About
                            : (await templateRendering.RenderTextTemplateAsync(config.DescriptionTemplate, cancellationToken)).Trim();
                        var username = NormalizeUsername(await templateRendering.RenderTextTemplateAsync(config.UsernameTemplate, cancellationToken));
                        ValidateUsername(username);

                        await channelService.UpdateChannelInfoAsync(account.Id, channel.TelegramId, title, about);
                        await ApplyAvatarIfNeededAsync(config, channel.TelegramId, true, account.Id, templateRendering, assetStorage, channelService, groupService, cancellationToken);
                        await channelService.SetChannelVisibilityAsync(account.Id, channel.TelegramId, true, username);

                        channel.Title = title;
                        channel.About = about;
                        channel.Username = username;
                        channel.SyncedAt = DateTime.UtcNow;
                        await channelManagement.UpdateChannelAsync(channel);

                        publicCount++;
                        processedForAccount++;
                        processed++;
                        await host.UpdateProgressAsync(processed, failed, cancellationToken);
                    }
                    catch
                    {
                        processedForAccount++;
                        processed++;
                        failed++;
                        await host.UpdateProgressAsync(processed, failed, cancellationToken);
                    }

                    if (processedForAccount < config.PerAccountBatchSize && publicCount < config.MaxPublicCount)
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
        }
        else
        {
            var groups = (await groupManagement.GetAllGroupsAsync()).ToList();
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                    return;

                var publicCount = groups.Count(x => x.CreatorAccountId == account.Id && !string.IsNullOrWhiteSpace(x.Username));
                var candidates = groups
                    .Where(x => x.CreatorAccountId == account.Id)
                    .Where(x => string.IsNullOrWhiteSpace(x.Username))
                    .Where(x => x.CategoryId == config.GroupCategoryId)
                    .Where(x => x.SystemCreatedAtUtc.HasValue && x.SystemCreatedAtUtc.Value <= now.AddDays(-config.MinSystemCreatedDays))
                    .OrderBy(x => x.SystemCreatedAtUtc)
                    .ThenBy(x => x.Id)
                    .ToList();

                var processedForAccount = 0;
                foreach (var group in candidates)
                {
                    if (processedForAccount >= config.PerAccountBatchSize || publicCount >= config.MaxPublicCount)
                        break;

                    try
                    {
                        var title = (await templateRendering.RenderTextTemplateAsync(config.TitleTemplate, cancellationToken)).Trim();
                        var about = string.IsNullOrWhiteSpace(config.DescriptionTemplate)
                            ? group.About
                            : (await templateRendering.RenderTextTemplateAsync(config.DescriptionTemplate, cancellationToken)).Trim();
                        var username = NormalizeUsername(await templateRendering.RenderTextTemplateAsync(config.UsernameTemplate, cancellationToken));
                        ValidateUsername(username);

                        await groupService.UpdateGroupInfoAsync(account.Id, group.TelegramId, title, about);
                        await ApplyAvatarIfNeededAsync(config, group.TelegramId, false, account.Id, templateRendering, assetStorage, channelService, groupService, cancellationToken);
                        await groupService.SetGroupVisibilityAsync(account.Id, group.TelegramId, true, username);

                        group.Title = title;
                        group.About = about;
                        group.Username = username;
                        group.SyncedAt = DateTime.UtcNow;
                        await groupManagement.UpdateGroupAsync(group);

                        publicCount++;
                        processedForAccount++;
                        processed++;
                        await host.UpdateProgressAsync(processed, failed, cancellationToken);
                    }
                    catch
                    {
                        processedForAccount++;
                        processed++;
                        failed++;
                        await host.UpdateProgressAsync(processed, failed, cancellationToken);
                    }

                    if (processedForAccount < config.PerAccountBatchSize && publicCount < config.MaxPublicCount)
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
        }

        await taskManagement.UpdateTaskDraftAsync(host.TaskId, processed, host.Config);
    }

    private static async Task ApplyAvatarIfNeededAsync(
        ChannelGroupPublicizeTaskConfig config,
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

    private static ChannelGroupPublicizeTaskConfig Deserialize(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidOperationException("任务配置为空");

        return JsonSerializer.Deserialize<ChannelGroupPublicizeTaskConfig>(config)
               ?? throw new InvalidOperationException("任务配置解析失败");
    }

    private static void Validate(ChannelGroupPublicizeTaskConfig config)
    {
        config.CategoryIds = (config.CategoryIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        if (config.CategoryIds.Count == 0)
            throw new InvalidOperationException("请至少选择一个账号分类");

        config.TargetType = string.Equals(config.TargetType, ChannelGroupAutomationTaskObjectTypes.Group, StringComparison.OrdinalIgnoreCase)
            ? ChannelGroupAutomationTaskObjectTypes.Group
            : ChannelGroupAutomationTaskObjectTypes.Channel;

        config.TitleTemplate = (config.TitleTemplate ?? string.Empty).Trim();
        config.DescriptionTemplate = (config.DescriptionTemplate ?? string.Empty).Trim();
        config.UsernameTemplate = (config.UsernameTemplate ?? string.Empty).Trim();
        if (config.TitleTemplate.Length == 0)
            throw new InvalidOperationException("标题模板不能为空");
        if (config.UsernameTemplate.Length == 0)
            throw new InvalidOperationException("公开用户名模板不能为空");

        config.MinSystemCreatedDays = Math.Max(0, config.MinSystemCreatedDays);
        config.MaxPublicCount = Math.Max(1, config.MaxPublicCount);
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

    private static string NormalizeUsername(string value)
    {
        return (value ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
    }

    private static void ValidateUsername(string username)
    {
        if (!UsernameRegex.IsMatch(username))
            throw new InvalidOperationException("公开用户名不合法，必须为 5-32 位小写字母/数字/下划线，且以字母开头");
    }
}
