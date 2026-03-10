using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 计划任务仓储接口。
/// </summary>
public interface IScheduledTaskRepository : IRepository<ScheduledTask>
{
    Task<IReadOnlyList<ScheduledTask>> GetAllOrderedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default);
}
