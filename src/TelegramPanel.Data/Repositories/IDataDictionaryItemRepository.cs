using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 数据字典项仓储接口。
/// </summary>
public interface IDataDictionaryItemRepository : IRepository<DataDictionaryItem>
{
    Task<IReadOnlyList<DataDictionaryItem>> GetByDictionaryIdAsync(int dictionaryId, CancellationToken cancellationToken = default);
    Task DeleteByDictionaryIdAsync(int dictionaryId, CancellationToken cancellationToken = default);
}
