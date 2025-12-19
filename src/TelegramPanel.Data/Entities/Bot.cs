namespace TelegramPanel.Data.Entities;

/// <summary>
/// Telegram 机器人（Bot）实体
/// </summary>
public class Bot
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Token { get; set; } = null!;
    public string? Username { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncAt { get; set; }
    public long? LastUpdateId { get; set; }

    public ICollection<BotChannelCategory> Categories { get; set; } = new List<BotChannelCategory>();
    public ICollection<BotChannel> Channels { get; set; } = new List<BotChannel>();
}
