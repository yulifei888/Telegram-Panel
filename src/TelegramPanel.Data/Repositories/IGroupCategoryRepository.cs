using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组分类仓储接口
/// </summary>
public interface IGroupCategoryRepository : IRepository<GroupCategory>
{
    Task<GroupCategory?> GetByNameAsync(string name);
}
