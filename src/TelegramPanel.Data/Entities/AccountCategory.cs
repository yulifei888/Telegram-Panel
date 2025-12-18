namespace TelegramPanel.Data.Entities;

/// <summary>
/// 账号分类实体
/// </summary>
public class AccountCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}
