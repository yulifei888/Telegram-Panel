using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 频道仓储实现
/// </summary>
public class ChannelRepository : Repository<Channel>, IChannelRepository
{
    public ChannelRepository(AppDbContext context) : base(context)
    {
    }

    public override async Task<Channel?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public override async Task<IEnumerable<Channel>> GetAllAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<Channel?> GetByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Channel>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.CreatorAccountId == accountId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetByGroupAsync(int groupId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.GroupId == groupId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetBroadcastChannelsAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.IsBroadcast)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }
}
