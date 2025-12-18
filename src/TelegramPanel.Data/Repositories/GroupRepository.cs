using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组仓储实现
/// </summary>
public class GroupRepository : Repository<Group>, IGroupRepository
{
    public GroupRepository(AppDbContext context) : base(context)
    {
    }

    public override async Task<Group?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public override async Task<IEnumerable<Group>> GetAllAsync()
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .OrderByDescending(g => g.SyncedAt)
            .ToListAsync();
    }

    public async Task<Group?> GetByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .FirstOrDefaultAsync(g => g.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Group>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Where(g => g.CreatorAccountId == accountId)
            .OrderByDescending(g => g.SyncedAt)
            .ToListAsync();
    }
}
