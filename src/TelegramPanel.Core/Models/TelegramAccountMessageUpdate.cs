using TL;

namespace TelegramPanel.Core.Models;

public sealed record TelegramInlineButton(
    int Index,
    int RowIndex,
    int ColumnIndex,
    string Text,
    byte[] CallbackData);

public sealed record TelegramAccountMessageUpdate(
    int AccountId,
    DateTimeOffset ReceivedAtUtc,
    bool IsEdited,
    Message Message,
    long? SenderUserId,
    string? SenderUsername,
    bool SenderIsBot,
    int? ReplyToMessageId,
    int? ThreadId,
    IReadOnlyList<TelegramInlineButton> Buttons)
{
    public string Text => (Message.message ?? string.Empty).Trim();

    public bool HasVisualMedia => Message.media is MessageMediaPhoto or MessageMediaDocument;
}
