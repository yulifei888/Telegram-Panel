using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 通用仓储实现
/// </summary>
public class Repository<T> : IRepository<T> where T : class
{
    protected readonly AppDbContext _context;
    protected readonly DbSet<T> _dbSet;

    public Repository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    public virtual async Task<T?> GetByIdAsync(int id)
    {
        return await _dbSet.FindAsync(id);
    }

    public virtual async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _dbSet.ToListAsync();
    }

    public virtual async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
    {
        return await _dbSet.Where(predicate).ToListAsync();
    }

    public virtual async Task<T> AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
        await SaveChangesWithSqliteLockRetryAsync();
        return entity;
    }

    public virtual async Task UpdateAsync(T entity)
    {
        _dbSet.Update(entity);
        await SaveChangesWithSqliteLockRetryAsync();
    }

    public virtual async Task DeleteAsync(T entity)
    {
        _dbSet.Remove(entity);
        await SaveChangesWithSqliteLockRetryAsync();
    }

    public virtual async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null)
    {
        return predicate == null
            ? await _dbSet.CountAsync()
            : await _dbSet.CountAsync(predicate);
    }

    protected async Task SaveChangesWithSqliteLockRetryAsync(CancellationToken cancellationToken = default)
    {
        // SQLite 在云端（容器/卷/后台任务）场景更容易遇到瞬时写锁；这里做有限重试来提升稳定性
        const int maxAttempts = 5;
        var delayMs = 200;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (IsSqliteLock(ex))
            {
                if (attempt == maxAttempts)
                    throw;

                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
            catch (SqliteException ex) when (IsSqliteLock(ex))
            {
                if (attempt == maxAttempts)
                    throw;

                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
        }
    }

    private static bool IsSqliteLock(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SqliteException sqliteEx && (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6))
                return true;
        }

        var msg = GetInnermostMessage(ex);
        return msg.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("database is busy", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInnermostMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException != null)
            cur = cur.InnerException;
        return string.IsNullOrWhiteSpace(cur.Message) ? ex.Message : cur.Message;
    }
}
