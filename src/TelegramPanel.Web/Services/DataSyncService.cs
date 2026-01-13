using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 数据同步服务（页面层复用：Home/Settings/Channels）
/// </summary>
public class DataSyncService
{
    private readonly AccountManagementService _accountManagement;
    private readonly ChannelManagementService _channelManagement;
    private readonly GroupManagementService _groupManagement;
    private readonly IChannelService _channelService;
    private readonly IGroupService _groupService;
    private readonly AccountTelegramToolsService _telegramTools;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataSyncService> _logger;

    public DataSyncService(
        AccountManagementService accountManagement,
        ChannelManagementService channelManagement,
        GroupManagementService groupManagement,
        IChannelService channelService,
        IGroupService groupService,
        AccountTelegramToolsService telegramTools,
        IConfiguration configuration,
        ILogger<DataSyncService> logger)
    {
        _accountManagement = accountManagement;
        _channelManagement = channelManagement;
        _groupManagement = groupManagement;
        _channelService = channelService;
        _groupService = groupService;
        _telegramTools = telegramTools;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SyncSummary> SyncAllActiveAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountManagement.GetActiveAccountsAsync();
        return await SyncAccountsAsync(accounts, cancellationToken);
    }

    public async Task<SyncSummary> SyncAccountAsync(int accountId, CancellationToken cancellationToken)
    {
        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        return await SyncAccountsAsync(new[] { account }, cancellationToken);
    }

    public async Task<SyncSummary> SyncAccountsAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken)
    {
        var summary = new SyncSummary();

        var accountList = accounts.ToList();
        var delayMs = _configuration.GetValue("Telegram:DefaultDelayMs", 2000);
        if (delayMs < 0) delayMs = 0;
        if (delayMs > 60000) delayMs = 60000;

        for (var index = 0; index < accountList.Count; index++)
        {
            var account = accountList[index];
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 同步频道：仅同步“频道创建人=本账号”的频道
                var channelInfos = await _channelService.GetOwnedChannelsAsync(account.Id);
                var keepChannelIds = new List<int>(capacity: channelInfos.Count);

                foreach (var channelInfo in channelInfos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var channel = new TelegramPanel.Data.Entities.Channel
                    {
                        TelegramId = channelInfo.TelegramId,
                        AccessHash = channelInfo.AccessHash,
                        Title = channelInfo.Title,
                        Username = channelInfo.Username,
                        IsBroadcast = channelInfo.IsBroadcast,
                        MemberCount = channelInfo.MemberCount,
                        About = channelInfo.About,
                        CreatorAccountId = account.Id,
                        CreatedAt = channelInfo.CreatedAt
                    };

                    var saved = await _channelManagement.CreateOrUpdateChannelAsync(channel);
                    keepChannelIds.Add(saved.Id);

                    summary.TotalChannelsSynced++;
                }

                // 同步群组：保持原逻辑（仅创建的群组）
                var groups = await _groupService.GetOwnedGroupsAsync(account.Id);
                foreach (var groupInfo in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var group = new TelegramPanel.Data.Entities.Group
                    {
                        TelegramId = groupInfo.TelegramId,
                        AccessHash = groupInfo.AccessHash,
                        Title = groupInfo.Title,
                        Username = groupInfo.Username,
                        MemberCount = groupInfo.MemberCount,
                        About = null,
                        CreatorAccountId = account.Id
                    };

                    await _groupManagement.CreateOrUpdateGroupAsync(group);
                    summary.TotalGroupsSynced++;
                }

                await _accountManagement.UpdateLastSyncTimeAsync(account.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Account sync failed (debug details): {AccountId}", account.Id);
                summary.AccountFailures.Add((account.Id, account.Phone, ex.Message));

                // 同步失败时更新账号的 Telegram 状态
                try
                {
                    var (statusSummary, statusDetails) = AccountTelegramToolsService.MapTelegramException(ex);
                    _logger.LogWarning("Account sync failed: {Phone} {Summary}", account.Phone, statusSummary);

                    // FLOOD_WAIT 之类属于“限流/临时状态”，不代表账号异常：避免把正常账号标红。
                    // 同理：某些群组接口不支持也不应影响账号状态。
                    var shouldPersistStatus = true;
                    if (statusSummary.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)
                        || statusSummary.Contains("CHANNEL_MONOFORUM_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
                    {
                        shouldPersistStatus = false;
                    }

                    var updatedByProbe = false;
                    if (string.Equals(statusSummary, "连接失败", StringComparison.OrdinalIgnoreCase))
                    {
                        // 同步操作里遇到的“连接失败”有可能是误判：这里做一次轻量探测（等同于“刷新账号状态”不勾深度探测），避免把存活账号标成掉线。
                        try
                        {
                            var probe = await _telegramTools.RefreshAccountStatusAsync(account.Id, probeCreateChannel: false, cancellationToken: cancellationToken);
                            if (!string.Equals(probe.Summary, "连接失败", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(probe.Summary, "已取消", StringComparison.OrdinalIgnoreCase))
                            {
                                // RefreshAccountStatusAsync 内部已持久化账号状态，这里避免覆盖回“连接失败”。
                                updatedByProbe = true;
                            }
                        }
                        catch (Exception probeEx)
                        {
                            _logger.LogWarning(probeEx, "Account sync fallback status probe failed: {AccountId}", account.Id);
                        }
                    }

                    if (!updatedByProbe && shouldPersistStatus)
                    {
                        account.TelegramStatusOk = false;
                        account.TelegramStatusSummary = statusSummary;
                        account.TelegramStatusDetails = statusDetails;
                        account.TelegramStatusCheckedAtUtc = DateTime.UtcNow;
                        await _accountManagement.UpdateAccountAsync(account);
                    }
                }
                catch (Exception statusEx)
                {
                    _logger.LogWarning(statusEx, "Failed to update Telegram status for account {AccountId}", account.Id);
                }
            }

            // 降速：同步多个账号时插入延迟，降低触发 FLOOD_WAIT 的概率
            if (delayMs > 0 && index < accountList.Count - 1)
            {
                var jitter = Random.Shared.Next(0, Math.Min(500, delayMs + 1));
                await Task.Delay(delayMs + jitter, cancellationToken);
            }
        }

        return summary;
    }

    public sealed class SyncSummary
    {
        public int TotalChannelsSynced { get; set; }
        public int TotalGroupsSynced { get; set; }
        public List<(int AccountId, string Phone, string Error)> AccountFailures { get; } = new();
    }
}
