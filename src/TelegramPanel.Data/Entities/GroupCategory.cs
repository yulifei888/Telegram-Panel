namespace TelegramPanel.Data.Entities;

/// <summary>
/// 群组分类实体
/// </summary>
public class GroupCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public ICollection<Group> Groups { get; set; } = new List<Group>();
}
