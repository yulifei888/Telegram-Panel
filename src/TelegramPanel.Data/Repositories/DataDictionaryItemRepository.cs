using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 数据字典项仓储实现。
/// </summary>
public sealed class DataDictionaryItemRepository : Repository<DataDictionaryItem>, IDataDictionaryItemRepository
{
    public DataDictionaryItemRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<DataDictionaryItem>> GetByDictionaryIdAsync(int dictionaryId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(x => x.DataDictionaryId == dictionaryId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteByDictionaryIdAsync(int dictionaryId, CancellationToken cancellationToken = default)
    {
        var items = await _dbSet
            .Where(x => x.DataDictionaryId == dictionaryId)
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
            return;

        _dbSet.RemoveRange(items);
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
    }
}
