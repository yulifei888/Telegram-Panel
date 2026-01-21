using System.Text.Json.Serialization;

namespace TelegramPanel.Web.ExternalApi;

public sealed record KickRequest(
    [property: JsonPropertyName("user_id")] long UserId,
    [property: JsonPropertyName("permanent_ban")] bool? PermanentBan = null);

public sealed record KickResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("summary")] KickSummary Summary,
    [property: JsonPropertyName("results")] IReadOnlyList<KickResultItem> Results,
    [property: JsonPropertyName("task_id")] int? TaskId = null);

public sealed record KickSummary(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("success")] int Success,
    [property: JsonPropertyName("failed")] int Failed);

public sealed record KickResultItem(
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error);

public sealed class KickTaskLog
{
    [JsonPropertyName("api_name")]
    public string ApiName { get; set; } = "";

    /// <summary>
    /// 0=all bots
    /// </summary>
    [JsonPropertyName("bot_id")]
    public int BotId { get; set; }

    [JsonPropertyName("use_all_chats")]
    public bool UseAllChats { get; set; }

    [JsonPropertyName("chat_ids")]
    public List<long> ChatIds { get; set; } = new();

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("permanent_ban")]
    public bool PermanentBan { get; set; }

    [JsonPropertyName("requested_at_utc")]
    public DateTime RequestedAtUtc { get; set; }

    [JsonPropertyName("canceled")]
    public bool Canceled { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("results")]
    public List<KickResultItem>? Results { get; set; }
}

