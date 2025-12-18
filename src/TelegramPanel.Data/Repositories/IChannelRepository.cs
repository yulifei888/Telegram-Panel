using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 频道仓储接口
/// </summary>
public interface IChannelRepository : IRepository<Channel>
{
    Task<Channel?> GetByTelegramIdAsync(long telegramId);
    Task<IEnumerable<Channel>> GetByCreatorAccountAsync(int accountId);
    Task<IEnumerable<Channel>> GetByGroupAsync(int groupId);
    Task<IEnumerable<Channel>> GetBroadcastChannelsAsync();
}
