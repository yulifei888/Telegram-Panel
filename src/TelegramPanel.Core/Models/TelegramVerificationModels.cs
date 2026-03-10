namespace TelegramPanel.Core.Models;

public sealed record TelegramInlineButtonOption(int Index, string Text, byte[] CallbackData);

public sealed record TelegramVerificationMessageCandidate(
    int MessageId,
    string? Text,
    byte[]? ImageJpegBytes,
    IReadOnlyList<TelegramInlineButtonOption> Buttons,
    bool MentionsAccount,
    bool IsReplyToSentMessage,
    DateTime DateUtc);
