using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelCategoryRepository : IRepository<BotChannelCategory>
{
    Task<IEnumerable<BotChannelCategory>> GetAllOrderedAsync();
    Task<BotChannelCategory?> GetByNameAsync(string name);
}
