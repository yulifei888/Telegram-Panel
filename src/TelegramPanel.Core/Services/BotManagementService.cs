using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

public class BotManagementService
{
    private readonly IBotRepository _botRepository;
    private readonly IBotChannelRepository _botChannelRepository;
    private readonly IBotChannelCategoryRepository _categoryRepository;
    private readonly IBotChannelMemberRepository _memberRepository;
    private readonly ILogger<BotManagementService> _logger;

    public BotManagementService(
        IBotRepository botRepository,
        IBotChannelRepository botChannelRepository,
        IBotChannelCategoryRepository categoryRepository,
        IBotChannelMemberRepository memberRepository,
        ILogger<BotManagementService> logger)
    {
        _botRepository = botRepository;
        _botChannelRepository = botChannelRepository;
        _categoryRepository = categoryRepository;
        _memberRepository = memberRepository;
        _logger = logger;
    }

    public async Task<IEnumerable<Bot>> GetAllBotsAsync()
    {
        return await _botRepository.GetAllWithStatsAsync();
    }

    public async Task<Bot?> GetBotAsync(int id)
    {
        return await _botRepository.GetByIdAsync(id);
    }

    public async Task<Bot> CreateBotAsync(string name, string token, string? username = null)
    {
        name = (name ?? string.Empty).Trim();
        token = (token ?? string.Empty).Trim();
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim().TrimStart('@');

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("机器人名称不能为空", nameof(name));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("机器人 Token 不能为空", nameof(token));

        var existing = await _botRepository.GetByNameAsync(name);
        if (existing != null)
            throw new InvalidOperationException("机器人名称已存在");

        var bot = new Bot
        {
            Name = name,
            Token = token,
            Username = username,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        return await _botRepository.AddAsync(bot);
    }

    public async Task UpdateBotAsync(Bot bot)
    {
        bot.Name = (bot.Name ?? string.Empty).Trim();
        bot.Token = (bot.Token ?? string.Empty).Trim();
        bot.Username = string.IsNullOrWhiteSpace(bot.Username) ? null : bot.Username.Trim().TrimStart('@');

        await _botRepository.UpdateAsync(bot);
    }

    public async Task SetBotActiveStatusAsync(int botId, bool isActive)
    {
        var bot = await _botRepository.GetByIdAsync(botId);
        if (bot == null)
            return;

        bot.IsActive = isActive;
        await _botRepository.UpdateAsync(bot);
    }

    public async Task DeleteBotAsync(int id)
    {
        var bot = await _botRepository.GetByIdAsync(id);
        if (bot == null)
            return;

        try
        {
            await _botRepository.DeleteAsync(bot);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 视为已被删除（幂等）
        }
    }

    public async Task<IEnumerable<BotChannelCategory>> GetCategoriesAsync()
    {
        return await _categoryRepository.GetAllOrderedAsync();
    }

    public async Task<BotChannelCategory> CreateCategoryAsync(string name, string? description = null)
    {
        name = (name ?? string.Empty).Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("分类名称不能为空", nameof(name));

        var existing = await _categoryRepository.GetByNameAsync(name);
        if (existing != null)
            throw new InvalidOperationException("分类名称已存在");

        var cat = new BotChannelCategory
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        return await _categoryRepository.AddAsync(cat);
    }

    public async Task DeleteCategoryAsync(int categoryId)
    {
        var cat = await _categoryRepository.GetByIdAsync(categoryId);
        if (cat == null)
            return;

        await _categoryRepository.DeleteAsync(cat);
    }

    public async Task<IEnumerable<BotChannel>> GetChannelsAsync(int botId, int? categoryId = null)
    {
        // Bot 频道列表仅展示“频道”（不展示群组/超级群组）
        var list = await _botChannelRepository.GetForBotAsync(botId, categoryId);
        return list.Where(x => x.IsBroadcast);
    }

    public async Task<(IReadOnlyList<BotChannel> Items, int TotalCount)> QueryChannelsPagedAsync(
        int botId,
        int? categoryId,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return await _botChannelRepository.QueryPagedAsync(
            botId: botId,
            categoryId: categoryId,
            broadcastOnly: true,
            search: search,
            pageIndex: pageIndex,
            pageSize: pageSize,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 获取 Bot 可管理的聊天列表（包含频道/群组/超级群组）。
    /// </summary>
    public async Task<IEnumerable<BotChannel>> GetChatsAsync(int botId, int? categoryId = null)
    {
        return await _botChannelRepository.GetForBotAsync(botId, categoryId);
    }

    public async Task<BotChannel?> GetChannelByTelegramIdAsync(int botId, long telegramId)
    {
        return await _botChannelRepository.GetByTelegramIdAsync(botId, telegramId);
    }

    public async Task<BotChannel> UpsertChannelAsync(int botId, BotChannel channel)
    {
        if (botId <= 0)
            throw new ArgumentException("BotId 无效", nameof(botId));
        if (channel.TelegramId == 0)
            throw new ArgumentException("TelegramId 无效", nameof(channel));

        var now = DateTime.UtcNow;

        var existing = await _botChannelRepository.GetGlobalByTelegramIdAsync(channel.TelegramId);
        if (existing == null)
        {
            channel.SyncedAt = now;
            existing = await _botChannelRepository.AddAsync(channel);
        }
        else
        {
            existing.Title = channel.Title;
            existing.Username = channel.Username;
            existing.IsBroadcast = channel.IsBroadcast;
            existing.MemberCount = channel.MemberCount;
            existing.About = channel.About;
            existing.AccessHash = channel.AccessHash;
            if (channel.CreatedAt.HasValue)
                existing.CreatedAt = channel.CreatedAt;
            existing.SyncedAt = now;

            await _botChannelRepository.UpdateAsync(existing);
        }

        await _memberRepository.UpsertAsync(botId, existing.Id, now);
        return existing;
    }

    public async Task DeleteChannelByTelegramIdAsync(int botId, long telegramId)
    {
        var ch = await _botChannelRepository.GetByTelegramIdAsync(botId, telegramId);
        if (ch == null)
            return;

        await _memberRepository.DeleteAsync(botId, ch.Id);

        // 清理无任何 Bot 关联的“孤儿频道”
        var remains = await _memberRepository.CountForChannelAsync(ch.Id);
        if (remains == 0)
            await _botChannelRepository.DeleteAsync(ch);
    }

    public async Task SetChannelCategoryAsync(int botChannelId, int? categoryId)
    {
        var ch = await _botChannelRepository.GetByIdAsync(botChannelId);
        if (ch == null)
            return;

        ch.CategoryId = categoryId;
        await _botChannelRepository.UpdateAsync(ch);
    }
}
