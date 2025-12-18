using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 频道数据管理服务
/// </summary>
public class ChannelManagementService
{
    private readonly IChannelRepository _channelRepository;

    public ChannelManagementService(IChannelRepository channelRepository)
    {
        _channelRepository = channelRepository;
    }

    public async Task<Channel?> GetChannelAsync(int id)
    {
        return await _channelRepository.GetByIdAsync(id);
    }

    public async Task<Channel?> GetChannelByTelegramIdAsync(long telegramId)
    {
        return await _channelRepository.GetByTelegramIdAsync(telegramId);
    }

    public async Task<IEnumerable<Channel>> GetAllChannelsAsync()
    {
        return await _channelRepository.GetAllAsync();
    }

    public async Task<IEnumerable<Channel>> GetChannelsByCreatorAsync(int accountId)
    {
        return await _channelRepository.GetByCreatorAccountAsync(accountId);
    }

    public async Task<IEnumerable<Channel>> GetChannelsByGroupAsync(int groupId)
    {
        return await _channelRepository.GetByGroupAsync(groupId);
    }

    public async Task<IEnumerable<Channel>> GetBroadcastChannelsAsync()
    {
        return await _channelRepository.GetBroadcastChannelsAsync();
    }

    public async Task<Channel> CreateOrUpdateChannelAsync(Channel channel)
    {
        var existing = await _channelRepository.GetByTelegramIdAsync(channel.TelegramId);
        if (existing != null)
        {
            // 更新现有频道
            existing.Title = channel.Title;
            existing.Username = channel.Username;
            existing.IsBroadcast = channel.IsBroadcast;
            existing.MemberCount = channel.MemberCount;
            existing.About = channel.About;
            existing.AccessHash = channel.AccessHash;
            existing.GroupId = channel.GroupId;
            existing.SyncedAt = DateTime.UtcNow;

            await _channelRepository.UpdateAsync(existing);
            return existing;
        }
        else
        {
            // 创建新频道
            channel.SyncedAt = DateTime.UtcNow;
            return await _channelRepository.AddAsync(channel);
        }
    }

    public async Task DeleteChannelAsync(int id)
    {
        var channel = await _channelRepository.GetByIdAsync(id);
        if (channel != null)
        {
            await _channelRepository.DeleteAsync(channel);
        }
    }

    public async Task UpdateChannelGroupAsync(int channelId, int? groupId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel != null)
        {
            channel.GroupId = groupId;
            await _channelRepository.UpdateAsync(channel);
        }
    }

    public async Task<int> GetTotalChannelCountAsync()
    {
        return await _channelRepository.CountAsync();
    }

    public async Task<int> GetChannelCountByCreatorAsync(int accountId)
    {
        return await _channelRepository.CountAsync(c => c.CreatorAccountId == accountId);
    }
}
