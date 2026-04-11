using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 数据字典仓储接口。
/// </summary>
public interface IDataDictionaryRepository : IRepository<DataDictionary>
{
    Task<DataDictionary?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<DataDictionary?> GetWithItemsAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DataDictionary>> GetAllWithItemsAsync(CancellationToken cancellationToken = default);
}
