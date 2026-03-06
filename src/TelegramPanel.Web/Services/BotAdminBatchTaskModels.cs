namespace TelegramPanel.Web.Services;

public sealed class BotTaskChannelItem
{
    public long TelegramId { get; set; }
    public string Title { get; set; } = "";
}

public sealed class BotAdminTaskFailureItem
{
    public long ChannelTelegramId { get; set; }
    public string ChannelTitle { get; set; } = "";
    public string? Username { get; set; }
    public long? UserId { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BotChannelSetAdminsByAccountTaskConfig
{
    public int BotId { get; set; }
    public int SelectedAccountId { get; set; }
    public string AdminTitle { get; set; } = "Admin";
    public int DelayMs { get; set; } = 1500;
    public int Rights { get; set; }
    public List<string> Usernames { get; set; } = new();
    public List<BotTaskChannelItem> Channels { get; set; } = new();
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public List<BotAdminTaskFailureItem>? Failures { get; set; }
    public List<string>? FailureLines { get; set; }
    public bool Canceled { get; set; }
    public string? Error { get; set; }
}

public sealed class BotSetAdminsTaskConfig
{
    public int BotId { get; set; }
    public BotSetAdminsRightsPayload Rights { get; set; } = new();
    public List<long> UserIds { get; set; } = new();
    public List<BotTaskChannelItem> Channels { get; set; } = new();
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public List<BotAdminTaskFailureItem>? Failures { get; set; }
    public List<string>? FailureLines { get; set; }
    public bool Canceled { get; set; }
    public string? Error { get; set; }
}

public sealed class BotSetAdminsRightsPayload
{
    public bool ManageChat { get; set; }
    public bool ChangeInfo { get; set; }
    public bool PostMessages { get; set; }
    public bool EditMessages { get; set; }
    public bool DeleteMessages { get; set; }
    public bool InviteUsers { get; set; }
    public bool RestrictMembers { get; set; }
    public bool PinMessages { get; set; }
    public bool PromoteMembers { get; set; }
}

public static class BotAdminTaskFailureFormatter
{
    public static List<string> BuildLines(IReadOnlyList<BotAdminTaskFailureItem> failures)
    {
        if (failures.Count == 0)
            return new List<string>();

        static string NormalizeText(string? value, string fallback)
        {
            var text = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        static string BuildTargetLabel(BotAdminTaskFailureItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Username))
                return "@" + item.Username.Trim();
            if (item.UserId.HasValue && item.UserId.Value > 0)
                return item.UserId.Value.ToString();
            return "未知目标";
        }

        var lines = new List<string>();
        foreach (var channelGroup in failures
                     .GroupBy(x => new
                     {
                         Id = x.ChannelTelegramId,
                         Title = NormalizeText(x.ChannelTitle, "（未命名频道）")
                     })
                     .OrderBy(x => x.Key.Title, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Key.Id))
        {
            var channelLabel = string.Equals(channelGroup.Key.Title, channelGroup.Key.Id.ToString(), StringComparison.Ordinal)
                ? channelGroup.Key.Title
                : $"{channelGroup.Key.Title}（{channelGroup.Key.Id}）";
            var segments = new List<string>();

            var channelReasons = channelGroup
                .Where(x => string.IsNullOrWhiteSpace(x.Username) && !x.UserId.HasValue)
                .Select(x => NormalizeText(x.Reason, "失败"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (channelReasons.Count > 0)
                segments.Add(string.Join(" / ", channelReasons));

            var userReasons = channelGroup
                .Where(x => !string.IsNullOrWhiteSpace(x.Username) || x.UserId.HasValue)
                .GroupBy(BuildTargetLabel, StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    var reasons = x.Select(y => NormalizeText(y.Reason, "失败"))
                        .Distinct(StringComparer.Ordinal);
                    return $"{x.Key}（{string.Join(" / ", reasons)}）";
                })
                .ToList();
            if (userReasons.Count > 0)
                segments.Add($"用户失败：{string.Join("；", userReasons)}");

            if (segments.Count == 0)
                segments.Add("失败");

            lines.Add($"{channelLabel}：{string.Join("；", segments)}");
        }

        return lines;
    }
}
