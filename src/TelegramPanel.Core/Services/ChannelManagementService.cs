using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 频道数据管理服务
/// </summary>
public class ChannelManagementService
{
    private readonly IChannelRepository _channelRepository;
    private readonly IAccountChannelRepository _accountChannelRepository;

    public ChannelManagementService(IChannelRepository channelRepository, IAccountChannelRepository accountChannelRepository)
    {
        _channelRepository = channelRepository;
        _accountChannelRepository = accountChannelRepository;
    }

    public async Task<Channel?> GetChannelAsync(int id)
    {
        return await _channelRepository.GetByIdAsync(id);
    }

    public async Task<Channel?> GetChannelByTelegramIdAsync(long telegramId)
    {
        return await _channelRepository.GetByTelegramIdAsync(telegramId);
    }

    public async Task<IEnumerable<Channel>> GetAllChannelsAsync()
    {
        return await _channelRepository.GetAllAsync();
    }

    /// <summary>
    /// 用于列表展示：支持按账号查看该账号可见的全部频道，并按角色筛选。
    /// </summary>
    public async Task<(IReadOnlyList<Channel> Items, int TotalCount)> QueryChannelsForViewPagedAsync(
        int accountId,
        int? groupId,
        string? filterType,
        string? membershipRole,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return await _channelRepository.QueryForViewPagedAsync(accountId, groupId, filterType, membershipRole, search, pageIndex, pageSize, cancellationToken);
    }

    public async Task<IEnumerable<Channel>> GetChannelsByCreatorAsync(int accountId)
    {
        return await _channelRepository.GetByCreatorAccountAsync(accountId);
    }

    public async Task<IEnumerable<Channel>> GetChannelsByGroupAsync(int groupId)
    {
        return await _channelRepository.GetByGroupAsync(groupId);
    }

    public async Task<IEnumerable<Channel>> GetBroadcastChannelsAsync()
    {
        return await _channelRepository.GetBroadcastChannelsAsync();
    }

    public async Task<Channel> CreateOrUpdateChannelAsync(Channel channel)
    {
        if (channel.TelegramId <= 0)
            throw new ArgumentException("TelegramId 必须为正数", nameof(channel));

        var existing = await _channelRepository.GetByTelegramIdAsync(channel.TelegramId);
        if (existing != null)
        {
            // 更新现有频道
            existing.Title = channel.Title;
            existing.Username = channel.Username;
            existing.IsBroadcast = channel.IsBroadcast;
            existing.MemberCount = channel.MemberCount;
            existing.About = channel.About;
            existing.AccessHash = channel.AccessHash;
            if (channel.GroupId.HasValue)
                existing.GroupId = channel.GroupId;
            if (existing.CreatorAccountId == null && channel.CreatorAccountId != null)
                existing.CreatorAccountId = channel.CreatorAccountId;
            if (channel.CreatedAt.HasValue)
                existing.CreatedAt = channel.CreatedAt;
            if (existing.SystemCreatedAtUtc == null && channel.SystemCreatedAtUtc != null)
                existing.SystemCreatedAtUtc = channel.SystemCreatedAtUtc;
            existing.SyncedAt = DateTime.UtcNow;

            await _channelRepository.UpdateAsync(existing);
            return existing;
        }

        // 兼容历史数据：曾出现 TelegramId=0 的“占位频道”记录（例如早期创建后未正确落库 TelegramId）
        // 同步时若能精确匹配到占位记录，则更新它而不是插入新记录，避免列表重复。
        if (channel.CreatorAccountId.HasValue && channel.CreatorAccountId.Value > 0)
        {
            var creatorId = channel.CreatorAccountId.Value;
            var candidates = (await _channelRepository.FindAsync(c =>
                    c.TelegramId == 0
                    && c.CreatorAccountId == creatorId
                    && (
                        (!string.IsNullOrWhiteSpace(channel.Username) && c.Username == channel.Username)
                        || c.Title == channel.Title
                    )))
                .OrderByDescending(c => c.SyncedAt)
                .ToList();

            var legacy = candidates.FirstOrDefault();
            if (legacy != null)
            {
                // 清理多余的占位重复记录（无 TelegramId 的记录不具备业务价值）
                foreach (var extra in candidates.Skip(1))
                    await _channelRepository.DeleteAsync(extra);

                legacy.TelegramId = channel.TelegramId;
                legacy.AccessHash = channel.AccessHash;
                legacy.Title = channel.Title;
                legacy.Username = channel.Username;
                legacy.IsBroadcast = channel.IsBroadcast;
                legacy.MemberCount = channel.MemberCount;
                legacy.About = channel.About;
                if (channel.GroupId.HasValue)
                    legacy.GroupId = channel.GroupId;
                if (channel.CreatedAt.HasValue)
                    legacy.CreatedAt = channel.CreatedAt;
                if (legacy.SystemCreatedAtUtc == null && channel.SystemCreatedAtUtc != null)
                    legacy.SystemCreatedAtUtc = channel.SystemCreatedAtUtc;
                legacy.SyncedAt = DateTime.UtcNow;

                await _channelRepository.UpdateAsync(legacy);
                return legacy;
            }
        }

        // 创建新频道
        channel.SyncedAt = DateTime.UtcNow;
        return await _channelRepository.AddAsync(channel);
    }

    public async Task UpdateChannelAsync(Channel channel)
    {
        channel.SyncedAt = DateTime.UtcNow;
        await _channelRepository.UpdateAsync(channel);
    }

    public async Task DeleteChannelAsync(int id)
    {
        var channel = await _channelRepository.GetByIdAsync(id);
        if (channel != null)
        {
            await _channelRepository.DeleteAsync(channel);
        }
    }

    public async Task UpdateChannelGroupAsync(int channelId, int? groupId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel != null)
        {
            channel.GroupId = groupId;
            await _channelRepository.UpdateAsync(channel);
        }
    }

    public async Task<int> GetTotalChannelCountAsync()
    {
        return await _channelRepository.CountAsync();
    }

    public async Task<int> GetChannelCountByCreatorAsync(int accountId)
    {
        return await _channelRepository.CountAsync(c => c.CreatorAccountId == accountId);
    }

    public async Task UpsertAccountChannelAsync(int accountId, int channelId, bool isCreator, bool isAdmin, DateTime syncedAtUtc)
    {
        await _accountChannelRepository.UpsertAsync(new AccountChannel
        {
            AccountId = accountId,
            ChannelId = channelId,
            IsCreator = isCreator,
            IsAdmin = isAdmin,
            SyncedAt = syncedAtUtc
        });
    }

    public async Task DeleteStaleAccountChannelsAsync(int accountId, IReadOnlyCollection<int> keepChannelIds)
    {
        var keepSet = keepChannelIds.Count == 0
            ? new HashSet<int>()
            : keepChannelIds.ToHashSet();

        var staleChannelIds = (await _accountChannelRepository.GetByAccountAsync(accountId))
            .Where(x => !keepSet.Contains(x.ChannelId))
            .Select(x => x.ChannelId)
            .Distinct()
            .ToList();

        foreach (var channelId in staleChannelIds)
            await RemoveAccountChannelAsync(channelId, accountId);

        var staleChannelIdSet = staleChannelIds.ToHashSet();
        var creatorOnlyChannelIds = (await _channelRepository.FindAsync(x => x.CreatorAccountId == accountId))
            .Select(x => x.Id)
            .Where(id => !keepSet.Contains(id) && !staleChannelIdSet.Contains(id))
            .Distinct()
            .ToList();

        foreach (var channelId in creatorOnlyChannelIds)
            await DetachCreatorFromChannelAsync(channelId, accountId);

        await _channelRepository.DeleteOrphanedAsync();
    }

    public async Task<IReadOnlyList<AccountChannel>> GetAccountChannelMembershipsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _accountChannelRepository.GetByAccountAsync(accountId, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountChannel>> GetChannelAccountMembershipsAsync(int channelId, CancellationToken cancellationToken = default)
    {
        return await _accountChannelRepository.GetByChannelAsync(channelId, cancellationToken);
    }

    public async Task RemoveAccountChannelAsync(int channelId, int accountId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel == null)
            return;

        var hasRemainingRelations = channel.AccountChannels.Any(x => x.AccountId != accountId);
        var creatorRemoved = channel.CreatorAccountId == accountId;

        await _accountChannelRepository.DeleteAsync(accountId, channelId);

        if (creatorRemoved)
            channel.CreatorAccountId = null;

        if (!hasRemainingRelations && channel.CreatorAccountId == null)
        {
            await _channelRepository.DeleteAsync(channel);
            return;
        }

        if (!creatorRemoved)
            return;

        channel.SyncedAt = DateTime.UtcNow;
        await _channelRepository.UpdateAsync(channel);
    }

    private async Task DetachCreatorFromChannelAsync(int channelId, int accountId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel == null || channel.CreatorAccountId != accountId)
            return;

        channel.CreatorAccountId = null;

        if (channel.AccountChannels.Count == 0)
        {
            await _channelRepository.DeleteAsync(channel);
            return;
        }

        channel.SyncedAt = DateTime.UtcNow;
        await _channelRepository.UpdateAsync(channel);
    }

    /// <summary>
    /// 解析频道操作的执行账号：
    /// 优先使用 preferredAccountId，其次 CreatorAccountId，否则从关联表中挑选一个管理员账号。
    /// </summary>
    public async Task<int?> ResolveExecuteAccountIdAsync(Channel channel, int? preferredAccountId = null)
    {
        if (preferredAccountId.HasValue && preferredAccountId.Value > 0)
            return preferredAccountId.Value;

        if (channel.CreatorAccountId.HasValue)
            return channel.CreatorAccountId.Value;

        return await _accountChannelRepository.GetPreferredAdminAccountIdAsync(channel.Id);
    }
}


