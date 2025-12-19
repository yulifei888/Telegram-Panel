using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelCategoryRepository : Repository<BotChannelCategory>, IBotChannelCategoryRepository
{
    public BotChannelCategoryRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<BotChannelCategory>> GetForBotAsync(int botId)
    {
        return await _dbSet
            .Where(x => x.BotId == botId)
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<BotChannelCategory?> GetByNameAsync(int botId, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return await _dbSet.FirstOrDefaultAsync(x => x.BotId == botId && x.Name == name);
    }
}

