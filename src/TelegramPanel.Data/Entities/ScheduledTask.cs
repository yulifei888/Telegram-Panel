namespace TelegramPanel.Data.Entities;

/// <summary>
/// 计划任务状态常量。
/// </summary>
public static class ScheduledTaskStatuses
{
    public const string Enabled = "enabled";
    public const string Paused = "paused";
}

/// <summary>
/// Cron 计划任务定义。
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }
    public string TaskType { get; set; } = null!;
    public string Status { get; set; } = ScheduledTaskStatuses.Enabled;
    public int Total { get; set; }
    public string? ConfigJson { get; set; }
    public string CronExpression { get; set; } = null!;
    public DateTime? NextRunAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public int? LastBatchTaskId { get; set; }
    public string? OwnedAssetScopeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
