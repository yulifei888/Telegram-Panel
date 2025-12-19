namespace TelegramPanel.Data.Entities;

/// <summary>
/// Bot 加入的频道（用于管理“非创建者频道”）
/// </summary>
public class BotChannel
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public long TelegramId { get; set; }
    public long? AccessHash { get; set; }
    public string Title { get; set; } = null!;
    public string? Username { get; set; }
    public bool IsBroadcast { get; set; }
    public int MemberCount { get; set; }
    public string? About { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public int? CategoryId { get; set; }

    public Bot? Bot { get; set; }
    public BotChannelCategory? Category { get; set; }
}

