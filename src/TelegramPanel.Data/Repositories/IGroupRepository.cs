using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组仓储接口
/// </summary>
public interface IGroupRepository : IRepository<Group>
{
    Task<Group?> GetByTelegramIdAsync(long telegramId);
    Task<IEnumerable<Group>> GetByCreatorAccountAsync(int accountId);
    Task<int> DeleteOrphanedAsync(CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Group> Items, int TotalCount)> QueryForViewPagedAsync(
        int accountId,
        int? categoryId,
        string? filterType,
        string? membershipRole,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
