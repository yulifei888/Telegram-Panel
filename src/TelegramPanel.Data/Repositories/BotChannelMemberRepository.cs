using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelMemberRepository : Repository<BotChannelMember>, IBotChannelMemberRepository
{
    public BotChannelMemberRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<BotChannelMember?> GetAsync(int botId, int botChannelId)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.BotId == botId && x.BotChannelId == botChannelId);
    }

    public async Task UpsertAsync(int botId, int botChannelId, DateTime syncedAt)
    {
        var existing = await GetAsync(botId, botChannelId);
        if (existing != null)
        {
            existing.SyncedAt = syncedAt;
            _dbSet.Update(existing);
            await _context.SaveChangesAsync();
            return;
        }

        await _dbSet.AddAsync(new BotChannelMember
        {
            BotId = botId,
            BotChannelId = botChannelId,
            SyncedAt = syncedAt
        });
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int botId, int botChannelId)
    {
        var existing = await GetAsync(botId, botChannelId);
        if (existing == null)
            return;

        _dbSet.Remove(existing);
        await _context.SaveChangesAsync();
    }

    public async Task<int> CountForChannelAsync(int botChannelId)
    {
        return await _dbSet.CountAsync(x => x.BotChannelId == botChannelId);
    }
}

