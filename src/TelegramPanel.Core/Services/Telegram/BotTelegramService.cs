using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

public class BotTelegramService
{
    private readonly BotManagementService _botManagement;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BotTelegramService> _logger;

    public BotTelegramService(
        BotManagementService botManagement,
        IConfiguration configuration,
        ILogger<BotTelegramService> logger)
    {
        _botManagement = botManagement;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> SyncBotChannelsAsync(int botId, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        if (!TryGetTelegramApi(out var apiId, out var apiHash))
            throw new InvalidOperationException("请先在【系统设置】中配置全局 Telegram API（ApiId/ApiHash）");

        await using var client = CreateBotClient(bot, apiId, apiHash);
        await EnsureBotLoginAsync(client, bot.Token);

        var dialogs = await client.Messages_GetAllDialogs();
        var synced = 0;

        foreach (var (_, chat) in dialogs.chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chat is not TL.Channel channel || !channel.IsActive)
                continue;

            var memberCount = TryReadInt(channel, 0, "participants_count", "ParticipantsCount", "participantsCount");
            string? about = null;
            try
            {
                var full = await client.Channels_GetFullChannel(channel);
                memberCount = full.full_chat.ParticipantsCount;
                about = (full.full_chat as ChannelFull)?.about;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get full channel info for bot {BotId} channel {ChannelId}", botId, channel.id);
            }

            await _botManagement.UpsertChannelAsync(new BotChannel
            {
                BotId = bot.Id,
                TelegramId = channel.id,
                AccessHash = channel.access_hash,
                Title = channel.title,
                Username = channel.MainUsername,
                IsBroadcast = channel.IsChannel,
                MemberCount = memberCount,
                About = about,
                CreatedAt = channel.date
            });

            synced++;
        }

        bot.LastSyncAt = DateTime.UtcNow;
        await _botManagement.UpdateBotAsync(bot);

        return synced;
    }

    public async Task<string> GetPublicLinkAsync(int botId, long channelTelegramId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!TryGetTelegramApi(out var apiId, out var apiHash))
            throw new InvalidOperationException("请先在【系统设置】中配置全局 Telegram API（ApiId/ApiHash）");

        await using var client = CreateBotClient(bot, apiId, apiHash);
        await EnsureBotLoginAsync(client, bot.Token);

        var channel = await FindChannelAsync(client, channelTelegramId);
        if (channel == null)
            throw new InvalidOperationException($"频道不存在或机器人不可见：{channelTelegramId}");

        if (!string.IsNullOrWhiteSpace(channel.MainUsername))
            return $"https://t.me/{channel.MainUsername}";

        throw new InvalidOperationException("该频道没有公开用户名");
    }

    public async Task<string> ExportInviteLinkAsync(int botId, long channelTelegramId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!TryGetTelegramApi(out var apiId, out var apiHash))
            throw new InvalidOperationException("请先在【系统设置】中配置全局 Telegram API（ApiId/ApiHash）");

        await using var client = CreateBotClient(bot, apiId, apiHash);
        await EnsureBotLoginAsync(client, bot.Token);

        var channel = await FindChannelAsync(client, channelTelegramId);
        if (channel == null)
            throw new InvalidOperationException($"频道不存在或机器人不可见：{channelTelegramId}");

        if (!string.IsNullOrWhiteSpace(channel.MainUsername))
            return $"https://t.me/{channel.MainUsername}";

        var invite = await client.Messages_ExportChatInvite(channel);
        var link = invite switch
        {
            ChatInviteExported e => e.link,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(link))
            throw new InvalidOperationException("无法获取邀请链接（可能无权限）");

        return link;
    }

    public async Task<IReadOnlyDictionary<long, string>> ExportInviteLinksAsync(int botId, IReadOnlyList<long> channelTelegramIds, CancellationToken cancellationToken)
    {
        if (channelTelegramIds.Count == 0)
            return new Dictionary<long, string>();

        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!TryGetTelegramApi(out var apiId, out var apiHash))
            throw new InvalidOperationException("请先在【系统设置】中配置全局 Telegram API（ApiId/ApiHash）");

        await using var client = CreateBotClient(bot, apiId, apiHash);
        await EnsureBotLoginAsync(client, bot.Token);

        var dialogs = await client.Messages_GetAllDialogs();
        var channels = dialogs.chats.Values.OfType<TL.Channel>().Where(c => c.IsActive).ToDictionary(c => c.id, c => c);

        var map = new Dictionary<long, string>();
        foreach (var id in channelTelegramIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!channels.TryGetValue(id, out var ch))
                continue;

            if (!string.IsNullOrWhiteSpace(ch.MainUsername))
            {
                map[id] = $"https://t.me/{ch.MainUsername}";
                continue;
            }

            var invite = await client.Messages_ExportChatInvite(ch);
            var link = invite switch
            {
                ChatInviteExported e => e.link,
                _ => null
            };
            map[id] = string.IsNullOrWhiteSpace(link) ? "(无法获取邀请链接/无权限)" : link;
        }

        return map;
    }

    private bool TryGetTelegramApi(out int apiId, out string apiHash)
    {
        apiHash = (_configuration["Telegram:ApiHash"] ?? string.Empty).Trim();
        return int.TryParse(_configuration["Telegram:ApiId"], out apiId)
               && apiId > 0
               && !string.IsNullOrWhiteSpace(apiHash);
    }

    private Client CreateBotClient(Bot bot, int apiId, string apiHash)
    {
        var sessionsPath = _configuration["Telegram:BotSessionsPath"];
        if (string.IsNullOrWhiteSpace(sessionsPath))
        {
            var root = _configuration["Telegram:SessionsPath"] ?? "sessions";
            sessionsPath = Path.Combine(root, "bots");
        }

        Directory.CreateDirectory(sessionsPath);
        var sessionPath = Path.Combine(sessionsPath, $"bot_{bot.Id}.session");

        string Config(string what)
        {
            return what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => sessionPath,
                "session_key" => apiHash,
                "bot_token" => bot.Token,
                _ => null!
            };
        }

        return new Client(Config);
    }

    private static async Task EnsureBotLoginAsync(Client client, string token)
    {
        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Bot Token 为空");

        await client.ConnectAsync();

        // WTelegram: Bot 登录
        await client.LoginBotIfNeeded(token);

        if (client.User == null)
            throw new InvalidOperationException("机器人登录失败");
    }

    private static async Task<TL.Channel?> FindChannelAsync(Client client, long channelTelegramId)
    {
        var dialogs = await client.Messages_GetAllDialogs();
        return dialogs.chats.Values.OfType<TL.Channel>().FirstOrDefault(c => c.id == channelTelegramId);
    }

    private static int TryReadInt(object obj, int fallback, params string[] names)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name);
            if (prop != null && prop.CanRead)
            {
                var v = prop.GetValue(obj);
                if (v is int i) return i;
                if (v is long l) return unchecked((int)l);
                if (v is short s) return s;
                if (v is byte by) return by;
            }

            var field = type.GetField(name);
            if (field != null)
            {
                var v = field.GetValue(obj);
                if (v is int i) return i;
                if (v is long l) return unchecked((int)l);
                if (v is short s) return s;
                if (v is byte by) return by;
            }
        }

        return fallback;
    }
}
