using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelCategoryRepository : IRepository<BotChannelCategory>
{
    Task<IEnumerable<BotChannelCategory>> GetForBotAsync(int botId);
    Task<BotChannelCategory?> GetByNameAsync(int botId, string name);
}

