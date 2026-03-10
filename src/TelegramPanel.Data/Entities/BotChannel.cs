namespace TelegramPanel.Data.Entities;

/// <summary>
/// Bot 加入的频道（用于管理“非创建者频道”）
/// </summary>
public class BotChannel
{
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public long? AccessHash { get; set; }
    public string Title { get; set; } = null!;
    public string? Username { get; set; }
    public bool IsBroadcast { get; set; }
    public int MemberCount { get; set; }
    public string? About { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 频道状态检测结果：true=正常，false=异常，null=未检测
    /// </summary>
    public bool? ChannelStatusOk { get; set; }

    /// <summary>
    /// 最近一次频道状态检测时间（UTC）
    /// </summary>
    public DateTime? ChannelStatusCheckedAtUtc { get; set; }

    /// <summary>
    /// 最近一次检测失败原因（仅异常时有值）
    /// </summary>
    public string? ChannelStatusError { get; set; }

    public int? CategoryId { get; set; }

    public BotChannelCategory? Category { get; set; }
    public ICollection<BotChannelMember> Members { get; set; } = new List<BotChannelMember>();
}
