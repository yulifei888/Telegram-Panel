using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotRepository : Repository<Bot>, IBotRepository
{
    public BotRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<Bot?> GetByNameAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return await _dbSet.FirstOrDefaultAsync(x => x.Name == name);
    }

    public async Task<IEnumerable<Bot>> GetAllWithStatsAsync()
    {
        // 只用于列表显示统计信息，避免一次性加载全部频道详情
        return await _dbSet
            .Include(x => x.Channels)
            .Include(x => x.Categories)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }
}

