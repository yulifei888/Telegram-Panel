using System.ComponentModel.DataAnnotations.Schema;

namespace TelegramPanel.Data.Entities;

/// <summary>
/// 账号实体
/// </summary>
public class Account
{
    public int Id { get; set; }
    public string Phone { get; set; } = null!;

    private string? _displayPhone;

    /// <summary>
    /// 格式化后的手机号（用于 UI 展示，不持久化），格式如：+86 13800138000
    /// 如果未通过 AccountManagementService 设置，则回退到 Phone 的值
    /// </summary>
    [NotMapped]
    public string DisplayPhone
    {
        get => _displayPhone ?? Phone;
        set => _displayPhone = value;
    }

    public long UserId { get; set; }

    /// <summary>
    /// 账号昵称（Telegram 显示名称）
    /// </summary>
    public string? Nickname { get; set; }
    public string? Username { get; set; }
    public string? Remark { get; set; }
    public string SessionPath { get; set; } = null!;
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public int? CategoryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 通过 777000 系统通知最早消息时间估算的注册时间（非百分百准确）
    /// </summary>
    public DateTime? EstimatedRegistrationAt { get; set; }

    /// <summary>
    /// 最近一次尝试估算注册时间的时间（UTC）
    /// </summary>
    public DateTime? EstimatedRegistrationCheckedAtUtc { get; set; }

    /// <summary>
    /// 最后一次登录 Telegram 的时间（UTC），用于风控检查
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Telegram 状态检测结果摘要（用于页面刷新后仍可展示上次检测结论）
    /// </summary>
    public string? TelegramStatusSummary { get; set; }

    /// <summary>
    /// Telegram 状态检测详情（错误码/原因等）
    /// </summary>
    public string? TelegramStatusDetails { get; set; }

    /// <summary>
    /// Telegram 状态检测是否成功（Ok）
    /// </summary>
    public bool? TelegramStatusOk { get; set; }

    /// <summary>
    /// Telegram 状态检测时间（UTC）
    /// </summary>
    public DateTime? TelegramStatusCheckedAtUtc { get; set; }

    /// <summary>
    /// 二级密码（两步验证密码），用于系统保存以便后续修改
    /// </summary>
    public string? TwoFactorPassword { get; set; }

    /// <summary>
    /// 账号当前已同步的频道数量（用于列表展示，不持久化）
    /// </summary>
    [NotMapped]
    public int ChannelCount { get; set; }

    /// <summary>
    /// 账号当前已同步的群组数量（用于列表展示，不持久化）
    /// </summary>
    [NotMapped]
    public int GroupCount { get; set; }

    // 导航属性
    public AccountCategory? Category { get; set; }
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public ICollection<AccountChannel> AccountChannels { get; set; } = new List<AccountChannel>();
    public ICollection<Group> Groups { get; set; } = new List<Group>();
    public ICollection<AccountGroup> AccountGroups { get; set; } = new List<AccountGroup>();
}
