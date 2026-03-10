using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 群组服务实现
/// </summary>
public class GroupService : IGroupService
{
    private readonly ITelegramClientPool _clientPool;
    private readonly AccountManagementService _accountManagement;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GroupService> _logger;

    public GroupService(
        ITelegramClientPool clientPool,
        AccountManagementService accountManagement,
        IConfiguration configuration,
        ILogger<GroupService> logger)
    {
        _clientPool = clientPool;
        _accountManagement = accountManagement;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId)
    {
        var groups = await GetVisibleGroupsAsync(accountId);
        return groups.Where(x => x.IsCreator).ToList();
    }

    public async Task<GroupInfo> CreateGroupAsync(int accountId, string title, string about, bool isPublic = false, string? username = null)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        _logger.LogInformation("Creating group '{Title}' for account {AccountId}", title, accountId);

        if (isPublic)
        {
            username = username?.Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("公开群组需要设置用户名");
        }

        UpdatesBase updates;
        try
        {
            updates = await client.Channels_CreateChannel(
                title: title,
                about: about,
                broadcast: false,
                megagroup: true
            );
        }
        catch (RpcException ex) when (ex.Code == 420 && string.Equals(ex.Message, "FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Telegram 返回 FROZEN_METHOD_INVALID：当前 ApiId/ApiHash 或账号被 Telegram 限制调用创建群组接口。" +
                "建议：在【系统设置】更换全局 ApiId/ApiHash（推荐使用你自己在 my.telegram.org 申请的），" +
                "并重新导入/重新登录生成 session 后再试。",
                ex);
        }

        var group = updates.Chats.Values.OfType<Channel>().FirstOrDefault(c => !c.IsChannel)
            ?? throw new InvalidOperationException("群组创建失败");

        if (isPublic && !string.IsNullOrWhiteSpace(username))
        {
            var available = await client.Channels_CheckUsername(group, username);
            if (!available)
                throw new InvalidOperationException($"用户名 '{username}' 不可用");

            try
            {
                await client.Channels_UpdateUsername(group, username);
            }
            catch (RpcException ex) when (ex.Message.Contains("USERNAME_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase))
            {
                // 视为成功
            }
        }

        return new GroupInfo
        {
            TelegramId = group.id,
            AccessHash = group.access_hash,
            Title = group.title,
            Username = isPublic ? username : group.MainUsername,
            MemberCount = 0,
            About = about,
            CreatorAccountId = accountId,
            IsCreator = true,
            IsAdmin = true,
            CreatedAt = group.date,
            SyncedAt = DateTime.UtcNow
        };
    }

    public async Task<List<GroupInfo>> GetVisibleGroupsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);

        var groups = new List<GroupInfo>();
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: true);

        foreach (var (_, chat) in dialogs.chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 基础群组（Chat 类型，非 Channel）
            if (chat is Chat basicChat && basicChat.IsActive)
            {
                var memberCount = (int)basicChat.participants_count;
                var about = ReadString(basicChat, null, "about", "About");
                var username = ReadString(basicChat, null, "username", "Username", "MainUsername");
                var isCreator = false;
                var isAdmin = false;

                try
                {
                    var fullChat = await ExecuteTelegramRequestAsync(
                        accountId,
                        $"拉取基础群详情({basicChat.id})",
                        () => client.Messages_GetFullChat(basicChat.id),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    if (fullChat.full_chat is ChatFull cf)
                    {
                        about = ReadString(cf, about, "about", "About");
                        memberCount = Math.Max(memberCount, ReadInt(cf, memberCount, "participants_count", "ParticipantsCount", "participantsCount"));

                        if (cf.participants is ChatParticipants cp)
                        {
                            memberCount = Math.Max(memberCount, cp.participants.Count());
                            var self = cp.participants.FirstOrDefault(p => GetChatParticipantUserId(p) == client.User!.id);
                            isCreator = self is ChatParticipantCreator;
                            isAdmin = isCreator || self is ChatParticipantAdmin;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (TryGetFloodWaitSeconds(ex.Message, out var seconds))
                    {
                        _logger.LogWarning("GetFullChat hit rate limit, retry after {Seconds}s (chatId={ChatId})", seconds, basicChat.id);
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get full chat info for {ChatId}: {Error}", basicChat.id, ex.Message);
                        _logger.LogDebug(ex, "GetFullChat debug details (chatId={ChatId})", basicChat.id);
                    }
                }

                groups.Add(new GroupInfo
                {
                    TelegramId = basicChat.id,
                    Title = basicChat.title,
                    Username = username,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = isCreator ? accountId : null,
                    IsCreator = isCreator,
                    IsAdmin = isAdmin,
                    SyncedAt = DateTime.UtcNow
                });

                continue;
            }

            // 超级群组（Channel 类型但 megagroup=true，即 !IsChannel）
            if (chat is not Channel channel || !channel.IsActive || channel.IsChannel)
                continue;

            try
            {
                var isCreator =
                    ReadBool(channel, "creator", "Creator", "is_creator", "IsCreator")
                    || ReadFlagsHas(channel, flagName: "creator", memberNames: new[] { "flags", "Flags" });
                var adminRights = ReadObject(channel, "admin_rights", "AdminRights", "adminRights");
                var isAdmin = isCreator || adminRights != null;

                var memberCount = ReadInt(channel, 0, "participants_count", "ParticipantsCount", "participantsCount", "memberCount", "MemberCount");
                string? about = null;
                try
                {
                    var fullChannel = await ExecuteTelegramRequestAsync(
                        accountId,
                        $"拉取超级群详情({channel.id})",
                        () => client.Channels_GetFullChannel(channel),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    memberCount = fullChannel.full_chat.ParticipantsCount;
                    about = (fullChannel.full_chat as ChannelFull)?.about;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("CHANNEL_MONOFORUM_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping full info for group {GroupId}: {Error}", channel.id, ex.Message);
                    }
                    else if (TryGetFloodWaitSeconds(ex.Message, out var seconds))
                    {
                        _logger.LogWarning("GetFullGroup hit rate limit, retry after {Seconds}s (groupId={GroupId})", seconds, channel.id);
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get full group info for {GroupId}: {Error}", channel.id, ex.Message);
                        _logger.LogDebug(ex, "GetFullGroup debug details (groupId={GroupId})", channel.id);
                    }
                }

                groups.Add(new GroupInfo
                {
                    TelegramId = channel.id,
                    AccessHash = channel.access_hash,
                    Title = channel.title,
                    Username = channel.MainUsername,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = isCreator ? accountId : null,
                    IsCreator = isCreator,
                    IsAdmin = isAdmin,
                    CreatedAt = channel.date,
                    SyncedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get visible group info for {GroupId}", channel.id);
            }
        }

        _logger.LogInformation("Found {Count} visible groups for account {AccountId}", groups.Count, accountId);
        return groups;
    }

    private static bool TryGetFloodWaitSeconds(string message, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var idx = message.IndexOf("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var tail = message.Substring(idx + "FLOOD_WAIT_".Length);
        var num = new string(tail.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(num, out seconds))
            return false;

        if (seconds < 1) seconds = 1;
        if (seconds > 120) seconds = 120;
        return true;
    }

    private static bool ReadBool(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value is bool b)
                return b;
        }

        return false;
    }

    private static bool ReadFlagsHas(object obj, string flagName, string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            if (!TryReadMember(obj, memberName, out var value) || value == null)
                continue;

            if (value is Enum enumValue)
            {
                var flagEnum = TryGetEnumFlag(enumValue.GetType(), flagName);
                if (flagEnum != null)
                    return enumValue.HasFlag(flagEnum);
                continue;
            }

            if (value is int intFlags)
                return ReadNumericFlagsHas(obj, flagName, intFlags);
            if (value is long longFlags)
                return ReadNumericFlagsHas(obj, flagName, longFlags);
        }

        return false;
    }

    private static bool ReadNumericFlagsHas(object obj, string flagName, long flagsValue)
    {
        var flagsType = obj.GetType().GetNestedType("Flags");
        if (flagsType == null || !flagsType.IsEnum)
            return false;

        var flagEnum = TryGetEnumFlag(flagsType, flagName);
        if (flagEnum == null)
            return false;

        var flagValue = Convert.ToInt64(flagEnum);
        return (flagsValue & flagValue) == flagValue;
    }

    private static Enum? TryGetEnumFlag(Type enumType, string flagName)
    {
        var names = Enum.GetNames(enumType);
        var match = names.FirstOrDefault(n => string.Equals(n, flagName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return null;

        return (Enum)Enum.Parse(enumType, match);
    }

    private static int ReadInt(object obj, int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value))
            {
                if (value is int i) return i;
                if (value is long l) return unchecked((int)l);
                if (value is short s) return s;
                if (value is byte by) return by;
            }
        }

        return fallback;
    }

    private static long ReadLong(object obj, long fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value))
            {
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is short s) return s;
                if (value is byte by) return by;
            }
        }

        return fallback;
    }

    private static string? ReadString(object obj, string? fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value is string s)
                return s;
        }

        return fallback;
    }

    private static object? ReadObject(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value != null)
                return value;
        }

        return null;
    }

    private static bool TryReadMember(object obj, string name, out object? value)
    {
        var type = obj.GetType();

        var prop = type.GetProperty(name);
        if (prop != null && prop.CanRead)
        {
            value = prop.GetValue(obj);
            return true;
        }

        var field = type.GetField(name);
        if (field != null)
        {
            value = field.GetValue(obj);
            return true;
        }

        value = null;
        return false;
    }

    private static long GetChatParticipantUserId(object participant)
    {
        return ReadLong(participant, 0, "user_id", "UserId", "userId");
    }

    public async Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId)
    {
        var groups = await GetVisibleGroupsAsync(accountId);
        return groups.FirstOrDefault(g => g.TelegramId == groupId);
    }

    public async Task<bool> UpdateGroupInfoAsync(int accountId, long groupId, string title, string? about)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, CancellationToken.None);

        title = (title ?? string.Empty).Trim();
        about = string.IsNullOrWhiteSpace(about) ? null : about.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("群组标题不能为空", nameof(title));

        await client.Channels_EditTitle(group, title);
        if (about != null)
            await client.Messages_EditChatAbout(group, about);

        return true;
    }

    public async Task<bool> SetGroupVisibilityAsync(int accountId, long groupId, bool isPublic, string? username = null)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, CancellationToken.None);

        if (isPublic)
        {
            username = (username ?? string.Empty).Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("公开群组需要设置用户名");

            var current = (group.MainUsername ?? string.Empty).Trim();
            if (string.Equals(current, username, StringComparison.OrdinalIgnoreCase))
                return true;

            var available = await client.Channels_CheckUsername(group, username);
            if (!available)
                throw new InvalidOperationException($"用户名 '{username}' 不可用");

            try
            {
                await client.Channels_UpdateUsername(group, username);
            }
            catch (RpcException ex) when (ex.Message.Contains("USERNAME_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(group.MainUsername))
                return true;

            await client.Channels_UpdateUsername(group, string.Empty);
        }

        return true;
    }

    public async Task<bool> SetGroupPhotoAsync(
        int accountId,
        long groupId,
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, cancellationToken);

        if (fileStream == null)
            throw new ArgumentException("头像文件为空", nameof(fileStream));

        fileName = (fileName ?? "group_photo.jpg").Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "group_photo.jpg";

        try
        {
            await using var encoded = await TelegramImageProcessor.PrepareAvatarJpegAsync(fileStream, cancellationToken);
            var uploaded = await client.UploadFileAsync(encoded, fileName);
            if (uploaded == null)
                throw new InvalidOperationException("群组头像上传失败：上传结果为空");

            await client.Channels_EditPhoto(group, new InputChatUploadedPhoto
            {
                flags = InputChatUploadedPhoto.Flags.has_file,
                file = uploaded
            });

            return true;
        }
        catch (SixLabors.ImageSharp.UnknownImageFormatException)
        {
            throw new InvalidOperationException("不支持的图片格式（建议使用 JPG/PNG）");
        }
    }

    private async Task<Channel> GetEditableMegaGroupAsync(int accountId, Client client, long groupId, CancellationToken cancellationToken)
    {
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: true);

        var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
        var mega = dialogs.chats.Values.OfType<Channel>()
            .FirstOrDefault(x => x.IsActive && !x.IsChannel && x.id == normalizedId);
        if (mega != null)
            return mega;

        if (dialogs.chats.Values.OfType<Chat>().Any(x => x.IsActive && x.id == normalizedId))
            throw new InvalidOperationException("当前仅支持超级群组自动修改资料/公开状态");

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    public async Task<bool> LeaveGroupAsync(int accountId, long groupId)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId);
            var dialogs = await ExecuteTelegramRequestAsync(
                accountId,
                "拉取频道/群组对话列表",
                () => client.Messages_GetAllDialogs(),
                CancellationToken.None,
                resetClientOnTimeout: true);

            var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
            var chat = dialogs.chats.Values.FirstOrDefault(x =>
                x switch
                {
                    Chat basicChat => basicChat.id == normalizedId,
                    Channel channel => !channel.IsChannel && channel.id == normalizedId,
                    _ => false
                });

            if (chat == null)
                throw new InvalidOperationException($"群组 {groupId} not found");

            await ExecuteTelegramRequestAsync(
                accountId,
                $"退出群组({normalizedId})",
                () => client.LeaveChat(chat.ToInputPeer()),
                CancellationToken.None,
                resetClientOnTimeout: false);

            return true;
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    public async Task<bool> DisbandGroupAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            CancellationToken.None,
            resetClientOnTimeout: true);

        var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
        var chat = dialogs.chats.Values.FirstOrDefault(x =>
            x switch
            {
                Chat basicChat => basicChat.id == normalizedId,
                Channel channel => !channel.IsChannel && channel.id == normalizedId,
                _ => false
            });

        if (chat == null)
            throw new InvalidOperationException($"群组 {groupId} not found");

        if (chat is Chat)
            throw new InvalidOperationException("基础群暂不支持一键解散，请在 Telegram 客户端手动操作或先升级为超级群");

        if (chat is not Channel megaGroup)
            throw new InvalidOperationException("仅支持解散超级群");

        var input = new InputChannel(megaGroup.id, megaGroup.access_hash);
        await ExecuteTelegramRequestAsync(
            accountId,
            $"解散群组({normalizedId})",
            () => client.Channels_DeleteChannel(input),
            CancellationToken.None,
            resetClientOnTimeout: false);

        return true;
    }

    public async Task<string> ExportJoinLinkAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

        var basic = dialogs.chats.Values.OfType<Chat>().FirstOrDefault(c => c.IsActive && c.id == groupId);
        if (basic != null)
        {
            var invite = await client.Messages_ExportChatInvite(basic);
            var link = invite switch
            {
                ChatInviteExported e => e.link,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(link))
                throw new InvalidOperationException("无法导出群组邀请链接（可能无权限）");

            return link;
        }

        var mega = dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.IsActive && !c.IsChannel && c.id == groupId);
        if (mega != null)
        {
            if (!string.IsNullOrWhiteSpace(mega.MainUsername))
                return $"https://t.me/{mega.MainUsername}";

            var invite = await client.Messages_ExportChatInvite(mega);
            var link = invite switch
            {
                ChatInviteExported e => e.link,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(link))
                throw new InvalidOperationException("无法导出群组邀请链接（可能无权限）");

            return link;
        }

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    public async Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

        var basic = dialogs.chats.Values.OfType<Chat>().FirstOrDefault(c => c.IsActive && c.id == groupId);
        if (basic != null)
        {
            var fullChat = await client.Messages_GetFullChat(basic.id);
            if (fullChat.full_chat is not ChatFull cf || cf.participants is not ChatParticipants cp)
                return new List<ChannelAdminInfo>();

            var list = new List<ChannelAdminInfo>();
            foreach (var p in cp.participants)
            {
                long userId;
                var isCreator = p is ChatParticipantCreator;

                if (p is ChatParticipantCreator creator)
                {
                    userId = creator.user_id;
                }
                else if (p is ChatParticipantAdmin admin)
                {
                    userId = admin.user_id;
                }
                else
                {
                    continue;
                }

                fullChat.users.TryGetValue(userId, out var u);
                list.Add(new ChannelAdminInfo(
                    UserId: userId,
                    Username: u?.MainUsername,
                    FirstName: u?.first_name,
                    LastName: u?.last_name,
                    IsCreator: isCreator,
                    Rank: null
                ));
            }

            return list
                .OrderByDescending(x => x.IsCreator)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var mega = dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.IsActive && !c.IsChannel && c.id == groupId);
        if (mega != null)
        {
            var participants = await client.Channels_GetParticipants(mega, new ChannelParticipantsAdmins());
            var list = new List<ChannelAdminInfo>();

            foreach (var p in participants.participants)
            {
                long userId;
                string? rank = null;
                var isCreator = p is ChannelParticipantCreator;

                if (p is ChannelParticipantAdmin admin)
                {
                    userId = admin.user_id;
                    rank = string.IsNullOrWhiteSpace(admin.rank) ? null : admin.rank;
                }
                else if (p is ChannelParticipantCreator creator)
                {
                    userId = creator.user_id;
                    rank = string.IsNullOrWhiteSpace(creator.rank) ? null : creator.rank;
                }
                else
                {
                    continue;
                }

                participants.users.TryGetValue(userId, out var u);
                list.Add(new ChannelAdminInfo(
                    UserId: userId,
                    Username: u?.MainUsername,
                    FirstName: u?.first_name,
                    LastName: u?.last_name,
                    IsCreator: isCreator,
                    Rank: rank
                ));
            }

            return list
                .OrderByDescending(x => x.IsCreator)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    private async Task<WTelegram.Client> GetOrCreateConnectedClientAsync(int accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existing = _clientPool.GetClient(accountId);
        if (existing?.User != null)
            return existing;

        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        var apiId = ResolveApiId(account);
        var apiHash = ResolveApiHash(account);
        var sessionKey = ResolveSessionKey(account, apiHash);

        if (string.IsNullOrWhiteSpace(account.SessionPath))
            throw new InvalidOperationException("账号缺少 SessionPath，无法创建 Telegram 客户端");

        var absoluteSessionPath = Path.GetFullPath(account.SessionPath);
        if (System.IO.File.Exists(absoluteSessionPath) && LooksLikeSqliteSession(absoluteSessionPath))
        {
            var converted = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: account.ApiId,
                apiHash: account.ApiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger
            );

            if (!converted.Ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，本项目无法直接复用。" +
                    "已尝试从本地 json（例如 sessions/<手机号>.json 或 session数据/<手机号>/*.json）读取 session_string 自动转换但失败；" +
                    $"原因：{converted.Reason}。请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。");
            }
        }

        await _clientPool.RemoveClientAsync(accountId);
        var client = await _clientPool.GetOrCreateClientAsync(accountId, apiId, apiHash, account.SessionPath, sessionKey, account.Phone, account.UserId);

        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                "连接 Telegram",
                () => client.ConnectAsync(),
                cancellationToken,
                resetClientOnTimeout: true);

            if (client.User == null && (client.UserId != 0 || account.UserId != 0))
            {
                await ExecuteTelegramRequestAsync(
                    accountId,
                    "恢复 Telegram 登录状态",
                    () => client.LoginUserIfNeeded(reloginOnFailedResume: false),
                    cancellationToken,
                    resetClientOnTimeout: true);
            }
        }
        catch (Exception ex)
        {
            if (LooksLikeSessionApiMismatchOrCorrupted(ex))
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件无法解析（通常是 ApiId/ApiHash 与生成 session 时不一致，或 session 文件已损坏）。" +
                    "请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。",
                    ex);
            }

            throw new InvalidOperationException($"Telegram 会话加载失败：{ex.Message}", ex);
        }

        if (client.User == null)
            throw new InvalidOperationException("账号未登录或 session 已失效，请重新登录生成新的 session");

        return client;
    }

    private TimeSpan GetTelegramRequestTimeout()
    {
        var seconds = int.TryParse(_configuration["Telegram:RequestTimeoutSeconds"], out var parsedSeconds)
            ? parsedSeconds
            : 90;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 600));
    }

    private async Task ExecuteTelegramRequestAsync(
        int accountId,
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private async Task<T> ExecuteTelegramRequestAsync<T>(
        int accountId,
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            return await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private int ResolveApiId(TelegramPanel.Data.Entities.Account account)
    {
        if (int.TryParse(_configuration["Telegram:ApiId"], out var globalApiId) && globalApiId > 0)
            return globalApiId;
        if (account.ApiId > 0)
            return account.ApiId;
        throw new InvalidOperationException("未配置全局 ApiId，且账号缺少 ApiId");
    }

    private string ResolveApiHash(TelegramPanel.Data.Entities.Account account)
    {
        var global = _configuration["Telegram:ApiHash"];
        if (!string.IsNullOrWhiteSpace(global))
            return global.Trim();
        if (!string.IsNullOrWhiteSpace(account.ApiHash))
            return account.ApiHash.Trim();
        throw new InvalidOperationException("未配置全局 ApiHash，且账号缺少 ApiHash");
    }

    private static string ResolveSessionKey(TelegramPanel.Data.Entities.Account account, string apiHash)
    {
        return !string.IsNullOrWhiteSpace(account.ApiHash) ? account.ApiHash.Trim() : apiHash.Trim();
    }

    private static bool LooksLikeSessionApiMismatchOrCorrupted(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash/id/key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSqliteSession(string filePath)
    {
        return SessionDataConverter.LooksLikeSqliteSession(filePath);
    }
}
