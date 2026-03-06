using System.Security.Cryptography;
using System.Text;

namespace TelegramPanel.Core.Services.Telegram;

public static class WebhookTokenHelper
{
    public static string ToWebhookPathToken(string botToken)
    {
        botToken = (botToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(botToken))
            throw new ArgumentException("botToken 不能为空", nameof(botToken));

        var bytes = Encoding.UTF8.GetBytes(botToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsLikelyPlainBotToken(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0)
            return false;

        // Telegram bot token 典型格式：{bot_id}:{token}
        return value.Contains(':', StringComparison.Ordinal);
    }

    public static bool IsSha256Hex(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }
}

