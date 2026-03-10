namespace TelegramPanel.Core.BatchTasks;

/// <summary>
/// 批量任务类型常量（数据库中 BatchTask.TaskType 的取值）。
/// </summary>
public static class BatchTaskTypes
{
    // Bot 任务（现有）
    public const string Invite = "invite";
    public const string SetAdmin = "set_admin";
    public const string BotChannelSetAdminsByAccount = "bot_channel_set_admins_by_account";
    public const string BotSetAdmins = "bot_set_admins";

    // User 任务（新增）
    public const string UserJoinSubscribe = "user_join_subscribe";
    public const string UserChatActive = "user_chat_active";
    public const string ChannelGroupPrivateCreate = "channel_group_private_create";
    public const string ChannelGroupPublicize = "channel_group_publicize";

    // System 任务（记录到任务中心）
    public const string AccountAutoSync = "account_auto_sync";

    // External API（记录到任务中心）
    public const string ExternalApiKick = "external_api_kick";
}
