namespace TelegramPanel.Data.Entities;

/// <summary>
/// 频道分组实体
/// </summary>
public class ChannelGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
}
