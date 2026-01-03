namespace TelegramPanel.Data.Entities;

/// <summary>
/// Bot - 频道/群 成员关系（用于记录“哪个 Bot 在哪个聊天里”）。
/// </summary>
public class BotChannelMember
{
    public int Id { get; set; }

    public int BotId { get; set; }
    public int BotChannelId { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public Bot Bot { get; set; } = null!;
    public BotChannel BotChannel { get; set; } = null!;
}

