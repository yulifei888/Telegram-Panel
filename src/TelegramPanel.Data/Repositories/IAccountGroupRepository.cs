using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号-群组关联仓储接口
/// </summary>
public interface IAccountGroupRepository : IRepository<AccountGroup>
{
    Task<AccountGroup?> GetAsync(int accountId, int groupId);
    Task<IReadOnlyList<AccountGroup>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountGroup>> GetByGroupAsync(int groupId, CancellationToken cancellationToken = default);
    Task UpsertAsync(AccountGroup link);
    Task DeleteAsync(int accountId, int groupId);
    Task DeleteForAccountExceptAsync(int accountId, IReadOnlyCollection<int> keepGroupIds);
    Task<int?> GetPreferredAdminAccountIdAsync(int groupId);
}
