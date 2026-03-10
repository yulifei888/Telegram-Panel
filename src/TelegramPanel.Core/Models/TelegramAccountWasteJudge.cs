namespace TelegramPanel.Core.Models;

/// <summary>
/// “废号”判定：用于在批量清理时识别可直接删除的账号。
/// </summary>
public static class TelegramAccountWasteJudge
{
    public static bool IsWaste(TelegramAccountStatusResult? status) => TryGetWasteReason(status, out _);

    public static bool TryGetWasteReason(TelegramAccountStatusResult? status, out string reason)
    {
        reason = string.Empty;

        if (status == null)
            return false;

        // Profile 兜底（理论上 Summary 已覆盖，但保持稳健）
        if (status.Profile is { IsDeleted: true })
        {
            reason = "账号已注销/被删除";
            return true;
        }

        if (status.Profile is { IsRestricted: true })
        {
            reason = "账号受限（Restricted）";
            return true;
        }

        var summary = (status.Summary ?? string.Empty).Trim();
        if (summary.Length == 0)
            return false;

        // 1) 明确的封禁/失效
        if (summary.Contains("账号被封禁", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("被封禁/停用", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("PHONE_NUMBER_BANNED", StringComparison.OrdinalIgnoreCase))
        {
            reason = "账号被封禁/停用";
            return true;
        }

        // 连接失败/探测失败：在“出售前清理废号”的场景下，用户预期将其视为不可用账号直接清理。
        if (summary.Contains("连接失败", StringComparison.OrdinalIgnoreCase))
        {
            reason = "连接失败（无法连通 Telegram）";
            return true;
        }

        if (summary.Contains("创建频道探测失败", StringComparison.OrdinalIgnoreCase))
        {
            reason = "创建频道探测失败（创建频道能力异常）";
            return true;
        }

        if (summary.Contains("Session 失效", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Session 失效";
            return true;
        }

        // Session 冲突/撤销：实际使用中通常都需要重新登录生成新 session，否则无法稳定使用
        if (summary.Contains("Session 冲突", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("AUTH_KEY_DUPLICATED", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Session 冲突";
            return true;
        }

        if (summary.Contains("Session 已被撤销", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Session 已被撤销";
            return true;
        }

        if (summary.Contains("Session 无法读取", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Session 损坏/不匹配";
            return true;
        }

        // 2) 受限/冻结/未登录
        if (summary.Contains("账号受限", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Restricted", StringComparison.OrdinalIgnoreCase))
        {
            reason = "账号受限（Restricted）";
            return true;
        }

        if (summary.Contains("账号被冻结", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            reason = "账号被冻结";
            return true;
        }

        if (summary.Contains("需要两步验证密码", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase))
        {
            reason = "需要两步验证密码（未登录）";
            return true;
        }

        if (summary.Contains("账号已注销", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("被删除", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("已注销/被删除", StringComparison.OrdinalIgnoreCase))
        {
            reason = "账号已注销/被删除";
            return true;
        }

        return false;
    }
}
