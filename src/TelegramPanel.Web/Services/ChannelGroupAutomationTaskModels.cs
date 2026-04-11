using System.Text.Json.Serialization;

namespace TelegramPanel.Web.Services;

public static class ChannelGroupAutomationTaskObjectTypes
{
    public const string Channel = "channel";
    public const string Group = "group";
}

public static class ChannelGroupAutomationAvatarSourceModes
{
    public const string None = "none";
    public const string Fixed = "fixed";
    public const string Dictionary = "dictionary";
}

public sealed class ChannelGroupPrivateCreateTaskConfig
{
    [JsonPropertyName("category_ids")]
    public List<int> CategoryIds { get; set; } = new();

    [JsonPropertyName("category_names")]
    public List<string> CategoryNames { get; set; } = new();

    [JsonPropertyName("create_type")]
    public string CreateType { get; set; } = ChannelGroupAutomationTaskObjectTypes.Channel;

    [JsonPropertyName("channel_group_id")]
    public int? ChannelGroupId { get; set; }

    [JsonPropertyName("channel_group_name")]
    public string? ChannelGroupName { get; set; }

    [JsonPropertyName("group_category_id")]
    public int? GroupCategoryId { get; set; }

    [JsonPropertyName("group_category_name")]
    public string? GroupCategoryName { get; set; }

    [JsonPropertyName("system_created_limit")]
    public int SystemCreatedLimit { get; set; } = 10;

    [JsonPropertyName("per_account_batch_size")]
    public int PerAccountBatchSize { get; set; } = 1;

    [JsonPropertyName("min_delay_seconds")]
    public int MinDelaySeconds { get; set; } = 10;

    [JsonPropertyName("max_delay_seconds")]
    public int MaxDelaySeconds { get; set; } = 30;

    [JsonPropertyName("jitter_percent")]
    public int JitterPercent { get; set; } = 20;

    [JsonPropertyName("title_template")]
    public string TitleTemplate { get; set; } = string.Empty;

    [JsonPropertyName("avatar_source")]
    public string AvatarSource { get; set; } = ChannelGroupAutomationAvatarSourceModes.None;

    [JsonPropertyName("fixed_avatar_asset_path")]
    public string? FixedAvatarAssetPath { get; set; }

    [JsonPropertyName("avatar_dictionary_token")]
    public string? AvatarDictionaryToken { get; set; }

    [JsonPropertyName("asset_scope_id")]
    public string? AssetScopeId { get; set; }
}

public sealed class ChannelGroupPublicizeTaskConfig
{
    [JsonPropertyName("category_ids")]
    public List<int> CategoryIds { get; set; } = new();

    [JsonPropertyName("category_names")]
    public List<string> CategoryNames { get; set; } = new();

    [JsonPropertyName("target_type")]
    public string TargetType { get; set; } = ChannelGroupAutomationTaskObjectTypes.Channel;

    [JsonPropertyName("channel_group_id")]
    public int? ChannelGroupId { get; set; }

    [JsonPropertyName("channel_group_name")]
    public string? ChannelGroupName { get; set; }

    [JsonPropertyName("group_category_id")]
    public int? GroupCategoryId { get; set; }

    [JsonPropertyName("group_category_name")]
    public string? GroupCategoryName { get; set; }

    [JsonPropertyName("min_system_created_days")]
    public int MinSystemCreatedDays { get; set; }

    [JsonPropertyName("max_public_count")]
    public int MaxPublicCount { get; set; } = 10;

    [JsonPropertyName("per_account_batch_size")]
    public int PerAccountBatchSize { get; set; } = 1;

    [JsonPropertyName("min_delay_seconds")]
    public int MinDelaySeconds { get; set; } = 10;

    [JsonPropertyName("max_delay_seconds")]
    public int MaxDelaySeconds { get; set; } = 30;

    [JsonPropertyName("jitter_percent")]
    public int JitterPercent { get; set; } = 20;

    [JsonPropertyName("title_template")]
    public string TitleTemplate { get; set; } = string.Empty;

    [JsonPropertyName("description_template")]
    public string DescriptionTemplate { get; set; } = string.Empty;

    [JsonPropertyName("username_template")]
    public string UsernameTemplate { get; set; } = string.Empty;

    [JsonPropertyName("avatar_source")]
    public string AvatarSource { get; set; } = ChannelGroupAutomationAvatarSourceModes.None;

    [JsonPropertyName("fixed_avatar_asset_path")]
    public string? FixedAvatarAssetPath { get; set; }

    [JsonPropertyName("avatar_dictionary_token")]
    public string? AvatarDictionaryToken { get; set; }

    [JsonPropertyName("asset_scope_id")]
    public string? AssetScopeId { get; set; }
}
