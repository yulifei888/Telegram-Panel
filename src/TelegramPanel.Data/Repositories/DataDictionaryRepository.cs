using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 数据字典仓储实现。
/// </summary>
public sealed class DataDictionaryRepository : Repository<DataDictionary>, IDataDictionaryRepository
{
    public DataDictionaryRepository(AppDbContext context) : base(context)
    {
    }

    public override async Task<DataDictionary?> GetByIdAsync(int id)
    {
        return await GetWithItemsAsync(id);
    }

    public override async Task<IEnumerable<DataDictionary>> GetAllAsync()
    {
        return await GetAllWithItemsAsync();
    }

    public async Task<DataDictionary?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;

        return await _dbSet
            .Include(x => x.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
    }

    public async Task<DataDictionary?> GetWithItemsAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(x => x.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<DataDictionary>> GetAllWithItemsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(x => x.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .AsSplitQuery()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }
}
