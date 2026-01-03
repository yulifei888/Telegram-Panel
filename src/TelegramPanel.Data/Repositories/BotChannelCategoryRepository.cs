using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelCategoryRepository : Repository<BotChannelCategory>, IBotChannelCategoryRepository
{
    public BotChannelCategoryRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<BotChannelCategory>> GetAllOrderedAsync()
    {
        return await _dbSet
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<BotChannelCategory?> GetByNameAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return await _dbSet.FirstOrDefaultAsync(x => x.Name == name);
    }
}
