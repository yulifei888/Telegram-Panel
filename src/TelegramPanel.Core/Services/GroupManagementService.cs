using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 群组数据管理服务
/// </summary>
public class GroupManagementService
{
    private readonly IGroupRepository _groupRepository;

    public GroupManagementService(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
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

    public async Task<IEnumerable<Group>> GetGroupsByCreatorAsync(int accountId)
    {
        return await _groupRepository.GetByCreatorAccountAsync(accountId);
    }

    public async Task<Group> CreateOrUpdateGroupAsync(Group group)
    {
        var existing = await _groupRepository.GetByTelegramIdAsync(group.TelegramId);
        if (existing != null)
        {
            // 更新现有群组
            existing.Title = group.Title;
            existing.Username = group.Username;
            existing.MemberCount = group.MemberCount;
            existing.About = group.About;
            existing.AccessHash = group.AccessHash;
            existing.SyncedAt = DateTime.UtcNow;

            await _groupRepository.UpdateAsync(existing);
            return existing;
        }
        else
        {
            // 创建新群组
            group.SyncedAt = DateTime.UtcNow;
            return await _groupRepository.AddAsync(group);
        }
    }

    public async Task DeleteGroupAsync(int id)
    {
        var group = await _groupRepository.GetByIdAsync(id);
        if (group != null)
        {
            await _groupRepository.DeleteAsync(group);
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
}
