using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号-频道关联仓储实现
/// </summary>
public class AccountChannelRepository : Repository<AccountChannel>, IAccountChannelRepository
{
    public AccountChannelRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<AccountChannel?> GetAsync(int accountId, int channelId)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.AccountId == accountId && x.ChannelId == channelId);
    }

    public async Task<IReadOnlyList<AccountChannel>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Include(x => x.Channel)
                .ThenInclude(x => x.Group)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.IsAdmin)
            .ThenByDescending(x => x.SyncedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccountChannel>> GetByChannelAsync(int channelId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(x => x.ChannelId == channelId)
            .Include(x => x.Account)
                .ThenInclude(x => x.Category)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.IsAdmin)
            .ThenBy(x => x.AccountId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(AccountChannel link)
    {
        var existing = await GetAsync(link.AccountId, link.ChannelId);
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

    public async Task DeleteAsync(int accountId, int channelId)
    {
        var existing = await GetAsync(accountId, channelId);
        if (existing == null)
            return;

        _dbSet.Remove(existing);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteForAccountExceptAsync(int accountId, IReadOnlyCollection<int> keepChannelIds)
    {
        var keep = keepChannelIds.ToHashSet();
        var toDelete = await _dbSet
            .Where(x => x.AccountId == accountId && !keep.Contains(x.ChannelId))
            .ToListAsync();

        if (toDelete.Count == 0)
            return;

        _dbSet.RemoveRange(toDelete);
        await _context.SaveChangesAsync();
    }

    public async Task<int?> GetPreferredAdminAccountIdAsync(int channelId)
    {
        return await _dbSet
            .Where(x => x.ChannelId == channelId && x.IsAdmin)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.SyncedAt)
            .Select(x => (int?)x.AccountId)
            .FirstOrDefaultAsync();
    }
}
