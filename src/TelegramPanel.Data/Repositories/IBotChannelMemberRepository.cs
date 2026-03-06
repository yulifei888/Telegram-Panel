using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelMemberRepository : IRepository<BotChannelMember>
{
    Task<BotChannelMember?> GetAsync(int botId, int botChannelId);
    Task UpsertAsync(int botId, int botChannelId, DateTime syncedAt);
    Task DeleteAsync(int botId, int botChannelId);
    Task<int> DeleteByChannelAndBotsAsync(int botChannelId, IReadOnlyCollection<int> botIds);
    Task<int> CountForChannelAsync(int botChannelId);
}
