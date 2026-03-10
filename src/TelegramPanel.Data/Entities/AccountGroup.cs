namespace TelegramPanel.Data.Entities;

/// <summary>
/// 账号-群组关联
/// </summary>
public class AccountGroup
{
    public int Id { get; set; }

    public int AccountId { get; set; }
    public int GroupId { get; set; }

    /// <summary>
    /// 是否为创建者（拥有者）
    /// </summary>
    public bool IsCreator { get; set; }

    /// <summary>
    /// 是否为管理员（包含创建者）
    /// </summary>
    public bool IsAdmin { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public Account Account { get; set; } = null!;
    public Group Group { get; set; } = null!;
}
