using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号-群组关联仓储接口
/// </summary>
public interface IAccountGroupRepository : IRepository<AccountGroup>
{
    Task<AccountGroup?> GetAsync(int accountId, int groupId);
    Task UpsertAsync(AccountGroup link);
    Task DeleteForAccountExceptAsync(int accountId, IReadOnlyCollection<int> keepGroupIds);
    Task<int?> GetPreferredAdminAccountIdAsync(int groupId);
}
