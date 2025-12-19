using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotRepository : IRepository<Bot>
{
    Task<Bot?> GetByNameAsync(string name);
    Task<IEnumerable<Bot>> GetAllWithStatsAsync();
}

