using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelRepository : IRepository<BotChannel>
{
    Task<BotChannel?> GetByTelegramIdAsync(int botId, long telegramId);
    Task<IEnumerable<BotChannel>> GetForBotAsync(int botId, int? categoryId = null);
}

