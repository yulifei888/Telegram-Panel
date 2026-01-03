using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelMemberRepository : IRepository<BotChannelMember>
{
    Task<BotChannelMember?> GetAsync(int botId, int botChannelId);
    Task UpsertAsync(int botId, int botChannelId, DateTime syncedAt);
    Task DeleteAsync(int botId, int botChannelId);
    Task<int> CountForChannelAsync(int botChannelId);
}

