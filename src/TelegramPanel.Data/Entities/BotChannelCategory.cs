namespace TelegramPanel.Data.Entities;

/// <summary>
/// Bot 频道分类（独立于账号频道分类/分组）
/// </summary>
public class BotChannelCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BotChannel> Channels { get; set; } = new List<BotChannel>();
}
