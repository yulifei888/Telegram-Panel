using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 批量任务仓储实现
/// </summary>
public class BatchTaskRepository : Repository<BatchTask>, IBatchTaskRepository
{
    public BatchTaskRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<BatchTask?> GetFreshByIdAsync(int id)
    {
        DetachTrackedEntity(id);
        return await _dbSet.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task UpdateFreshAsync(BatchTask entity)
    {
        DetachTrackedEntity(entity.Id);
        _dbSet.Update(entity);
        await SaveChangesWithSqliteLockRetryAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetByStatusAsync(string status)
    {
        return await _dbSet
            .Where(t => t.Status == status)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetRunningTasksAsync()
    {
        return await _dbSet
            .Where(t => t.Status == "running")
            .OrderBy(t => t.StartedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20)
    {
        return await _dbSet
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
    {
        if (keepCount <= 0)
            return 0;

        var staleTasks = await _dbSet
            .Where(t => t.Status == "completed" || t.Status == "failed")
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(keepCount)
            .ToListAsync(cancellationToken);

        if (staleTasks.Count == 0)
            return 0;

        _dbSet.RemoveRange(staleTasks);
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
        return staleTasks.Count;
    }

    private void DetachTrackedEntity(int id)
    {
        foreach (var entry in _context.ChangeTracker.Entries<BatchTask>())
        {
            if (entry.Entity.Id == id)
                entry.State = EntityState.Detached;
        }
    }
}
