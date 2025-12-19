using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelRepository : Repository<BotChannel>, IBotChannelRepository
{
    public BotChannelRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<BotChannel?> GetByTelegramIdAsync(int botId, long telegramId)
    {
        return await _dbSet
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.BotId == botId && x.TelegramId == telegramId);
    }

    public async Task<IEnumerable<BotChannel>> GetForBotAsync(int botId, int? categoryId = null)
    {
        var query = _dbSet
            .Include(x => x.Category)
            .Where(x => x.BotId == botId);

        if (categoryId.HasValue)
            query = query.Where(x => x.CategoryId == categoryId.Value);

        return await query
            .OrderByDescending(x => x.SyncedAt)
            .ToListAsync();
    }
}

