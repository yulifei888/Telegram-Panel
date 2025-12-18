using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 批量任务仓储接口
/// </summary>
public interface IBatchTaskRepository : IRepository<BatchTask>
{
    Task<IEnumerable<BatchTask>> GetByStatusAsync(string status);
    Task<IEnumerable<BatchTask>> GetRunningTasksAsync();
    Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20);
}
