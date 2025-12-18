namespace TelegramPanel.Data.Entities;

/// <summary>
/// 群组实体
/// </summary>
public class Group
{
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public long? AccessHash { get; set; }
    public string Title { get; set; } = null!;
    public string? Username { get; set; }
    public int MemberCount { get; set; }
    public string? About { get; set; }
    public int CreatorAccountId { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public Account CreatorAccount { get; set; } = null!;
}
