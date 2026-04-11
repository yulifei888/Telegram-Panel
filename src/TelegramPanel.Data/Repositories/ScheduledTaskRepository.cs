using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 计划任务仓储实现。
/// </summary>
public sealed class ScheduledTaskRepository : Repository<ScheduledTask>, IScheduledTaskRepository
{
    public ScheduledTaskRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetAllOrderedAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(x => x.Status == ScheduledTaskStatuses.Enabled)
            .OrderBy(x => x.NextRunAtUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }
}
