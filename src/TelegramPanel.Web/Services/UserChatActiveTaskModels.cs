using System.Text.Json.Serialization;

namespace TelegramPanel.Web.Services;

public static class UserChatActiveTaskModes
{
    public const string Random = "random";
    public const string Queue = "queue";
}

public sealed class UserChatActiveTaskConfig
{
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("targets")]
    public List<string> Targets { get; set; } = new();

    [JsonPropertyName("dictionary")]
    public List<string> Dictionary { get; set; } = new();

    [JsonPropertyName("delay_min_ms")]
    public int DelayMinMs { get; set; } = 15000;

    [JsonPropertyName("delay_max_ms")]
    public int DelayMaxMs { get; set; } = 45000;

    [JsonPropertyName("account_mode")]
    public string AccountMode { get; set; } = UserChatActiveTaskModes.Random;

    [JsonPropertyName("message_mode")]
    public string MessageMode { get; set; } = UserChatActiveTaskModes.Random;

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = UserChatActiveTaskModes.Queue;

    [JsonPropertyName("max_messages")]
    public int MaxMessages { get; set; }

    [JsonPropertyName("canceled")]
    public bool Canceled { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("recent_failures")]
    public List<UserChatActiveTaskRuntimeFailure> RecentFailures { get; set; } = new();
}

public sealed class UserChatActiveTaskRuntimeFailure
{
    [JsonPropertyName("time_utc")]
    public DateTime TimeUtc { get; set; }

    [JsonPropertyName("account_id")]
    public int AccountId { get; set; }

    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}
