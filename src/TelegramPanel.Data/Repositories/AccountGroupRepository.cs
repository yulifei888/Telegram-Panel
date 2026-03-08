using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号-群组关联仓储实现
/// </summary>
public class AccountGroupRepository : Repository<AccountGroup>, IAccountGroupRepository
{
    public AccountGroupRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<AccountGroup?> GetAsync(int accountId, int groupId)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.AccountId == accountId && x.GroupId == groupId);
    }

    public async Task UpsertAsync(AccountGroup link)
    {
        var existing = await GetAsync(link.AccountId, link.GroupId);
        if (existing == null)
        {
            await _dbSet.AddAsync(link);
        }
        else
        {
            existing.IsCreator = link.IsCreator;
            existing.IsAdmin = link.IsAdmin;
            existing.SyncedAt = link.SyncedAt;
            _dbSet.Update(existing);
        }

        await _context.SaveChangesAsync();
    }

    public async Task DeleteForAccountExceptAsync(int accountId, IReadOnlyCollection<int> keepGroupIds)
    {
        var keep = keepGroupIds.ToHashSet();
        var toDelete = await _dbSet
            .Where(x => x.AccountId == accountId && !keep.Contains(x.GroupId))
            .ToListAsync();

        if (toDelete.Count == 0)
            return;

        _dbSet.RemoveRange(toDelete);
        await _context.SaveChangesAsync();
    }

    public async Task<int?> GetPreferredAdminAccountIdAsync(int groupId)
    {
        return await _dbSet
            .Where(x => x.GroupId == groupId && x.IsAdmin)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.SyncedAt)
            .Select(x => (int?)x.AccountId)
            .FirstOrDefaultAsync();
    }
}
