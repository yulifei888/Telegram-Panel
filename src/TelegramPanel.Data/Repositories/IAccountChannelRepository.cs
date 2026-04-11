using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号-频道关联仓储接口
/// </summary>
public interface IAccountChannelRepository : IRepository<AccountChannel>
{
    Task<AccountChannel?> GetAsync(int accountId, int channelId);
    Task<IReadOnlyList<AccountChannel>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountChannel>> GetByChannelAsync(int channelId, CancellationToken cancellationToken = default);
    Task UpsertAsync(AccountChannel link);
    Task DeleteAsync(int accountId, int channelId);
    Task DeleteForAccountExceptAsync(int accountId, IReadOnlyCollection<int> keepChannelIds);
    Task<int?> GetPreferredAdminAccountIdAsync(int channelId);
}
