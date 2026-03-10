using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 群组数据管理服务
/// </summary>
public class GroupManagementService
{
    private readonly IGroupRepository _groupRepository;
    private readonly IAccountGroupRepository _accountGroupRepository;

    public GroupManagementService(IGroupRepository groupRepository, IAccountGroupRepository accountGroupRepository)
    {
        _groupRepository = groupRepository;
        _accountGroupRepository = accountGroupRepository;
    }

    public async Task<Group?> GetGroupAsync(int id)
    {
        return await _groupRepository.GetByIdAsync(id);
    }

    public async Task<Group?> GetGroupByTelegramIdAsync(long telegramId)
    {
        return await _groupRepository.GetByTelegramIdAsync(telegramId);
    }

    public async Task<IEnumerable<Group>> GetAllGroupsAsync()
    {
        return await _groupRepository.GetAllAsync();
    }

    public async Task<(IReadOnlyList<Group> Items, int TotalCount)> QueryGroupsForViewPagedAsync(
        int accountId,
        int? categoryId,
        string? filterType,
        string? membershipRole,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return await _groupRepository.QueryForViewPagedAsync(accountId, categoryId, filterType, membershipRole, search, pageIndex, pageSize, cancellationToken);
    }

    public async Task<IEnumerable<Group>> GetGroupsByCreatorAsync(int accountId)
    {
        return await _groupRepository.GetByCreatorAccountAsync(accountId);
    }

    public async Task<Group> CreateOrUpdateGroupAsync(Group group)
    {
        if (group.TelegramId <= 0)
            throw new ArgumentException("TelegramId 必须为正数", nameof(group));

        var existing = await _groupRepository.GetByTelegramIdAsync(group.TelegramId);
        if (existing != null)
        {
            existing.Title = group.Title;
            existing.Username = group.Username;
            existing.MemberCount = group.MemberCount;
            existing.About = group.About;
            existing.AccessHash = group.AccessHash;
            if (group.CategoryId.HasValue)
                existing.CategoryId = group.CategoryId;
            if (existing.CreatorAccountId == null && group.CreatorAccountId != null)
                existing.CreatorAccountId = group.CreatorAccountId;
            if (group.CreatedAt.HasValue)
                existing.CreatedAt = group.CreatedAt;
            if (existing.SystemCreatedAtUtc == null && group.SystemCreatedAtUtc != null)
                existing.SystemCreatedAtUtc = group.SystemCreatedAtUtc;
            existing.SyncedAt = DateTime.UtcNow;

            await _groupRepository.UpdateAsync(existing);
            return existing;
        }

        group.SyncedAt = DateTime.UtcNow;
        return await _groupRepository.AddAsync(group);
    }

    public async Task UpdateGroupAsync(Group group)
    {
        group.SyncedAt = DateTime.UtcNow;
        await _groupRepository.UpdateAsync(group);
    }

    public async Task DeleteGroupAsync(int id)
    {
        var group = await _groupRepository.GetByIdAsync(id);
        if (group != null)
            await _groupRepository.DeleteAsync(group);
    }

    public async Task UpdateGroupCategoryAsync(int groupId, int? categoryId)
    {
        var group = await _groupRepository.GetByIdAsync(groupId);
        if (group != null)
        {
            group.CategoryId = categoryId;
            group.SyncedAt = DateTime.UtcNow;
            await _groupRepository.UpdateAsync(group);
        }
    }

    public async Task<int> GetTotalGroupCountAsync()
    {
        return await _groupRepository.CountAsync();
    }

    public async Task<int> GetGroupCountByCreatorAsync(int accountId)
    {
        return await _groupRepository.CountAsync(g => g.CreatorAccountId == accountId);
    }

    public async Task UpsertAccountGroupAsync(int accountId, int groupId, bool isCreator, bool isAdmin, DateTime syncedAtUtc)
    {
        await _accountGroupRepository.UpsertAsync(new AccountGroup
        {
            AccountId = accountId,
            GroupId = groupId,
            IsCreator = isCreator,
            IsAdmin = isAdmin,
            SyncedAt = syncedAtUtc
        });
    }

    public async Task DeleteStaleAccountGroupsAsync(int accountId, IReadOnlyCollection<int> keepGroupIds)
    {
        var keepSet = keepGroupIds.Count == 0
            ? new HashSet<int>()
            : keepGroupIds.ToHashSet();

        var staleGroupIds = (await _accountGroupRepository.GetByAccountAsync(accountId))
            .Where(x => !keepSet.Contains(x.GroupId))
            .Select(x => x.GroupId)
            .Distinct()
            .ToList();

        foreach (var groupId in staleGroupIds)
            await RemoveAccountGroupAsync(groupId, accountId);

        var staleGroupIdSet = staleGroupIds.ToHashSet();
        var creatorOnlyGroupIds = (await _groupRepository.FindAsync(x => x.CreatorAccountId == accountId))
            .Select(x => x.Id)
            .Where(id => !keepSet.Contains(id) && !staleGroupIdSet.Contains(id))
            .Distinct()
            .ToList();

        foreach (var groupId in creatorOnlyGroupIds)
            await DetachCreatorFromGroupAsync(groupId, accountId);

        await _groupRepository.DeleteOrphanedAsync();
    }

    public async Task<IReadOnlyList<AccountGroup>> GetAccountGroupMembershipsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _accountGroupRepository.GetByAccountAsync(accountId, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountGroup>> GetGroupAccountMembershipsAsync(int groupId, CancellationToken cancellationToken = default)
    {
        return await _accountGroupRepository.GetByGroupAsync(groupId, cancellationToken);
    }

    public async Task RemoveAccountGroupAsync(int groupId, int accountId)
    {
        var group = await _groupRepository.GetByIdAsync(groupId);
        if (group == null)
            return;

        var hasRemainingRelations = group.AccountGroups.Any(x => x.AccountId != accountId);
        var creatorRemoved = group.CreatorAccountId == accountId;

        await _accountGroupRepository.DeleteAsync(accountId, groupId);

        if (creatorRemoved)
            group.CreatorAccountId = null;

        if (!hasRemainingRelations && group.CreatorAccountId == null)
        {
            await _groupRepository.DeleteAsync(group);
            return;
        }

        if (!creatorRemoved)
            return;

        group.SyncedAt = DateTime.UtcNow;
        await _groupRepository.UpdateAsync(group);
    }

    private async Task DetachCreatorFromGroupAsync(int groupId, int accountId)
    {
        var group = await _groupRepository.GetByIdAsync(groupId);
        if (group == null || group.CreatorAccountId != accountId)
            return;

        group.CreatorAccountId = null;

        if (group.AccountGroups.Count == 0)
        {
            await _groupRepository.DeleteAsync(group);
            return;
        }

        group.SyncedAt = DateTime.UtcNow;
        await _groupRepository.UpdateAsync(group);
    }

    /// <summary>
    /// 解析群组操作的执行账号：
    /// 优先使用 preferredAccountId，其次 CreatorAccountId，否则从关联表中挑选一个管理员账号。
    /// </summary>
    public async Task<int?> ResolveExecuteAccountIdAsync(Group group, int? preferredAccountId = null)
    {
        if (preferredAccountId.HasValue && preferredAccountId.Value > 0)
            return preferredAccountId.Value;

        if (group.CreatorAccountId.HasValue)
            return group.CreatorAccountId.Value;

        return await _accountGroupRepository.GetPreferredAdminAccountIdAsync(group.Id);
    }
}


