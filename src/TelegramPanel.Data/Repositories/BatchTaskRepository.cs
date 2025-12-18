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
}
