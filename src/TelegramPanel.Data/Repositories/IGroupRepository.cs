using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组仓储接口
/// </summary>
public interface IGroupRepository : IRepository<Group>
{
    Task<Group?> GetByTelegramIdAsync(long telegramId);
    Task<IEnumerable<Group>> GetByCreatorAccountAsync(int accountId);
}
