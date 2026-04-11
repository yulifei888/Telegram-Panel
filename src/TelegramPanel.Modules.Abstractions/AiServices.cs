namespace TelegramPanel.Modules;

/// <summary>
/// 面板统一 AI 服务接口，供宿主任务与扩展模块复用。
/// </summary>
public interface ITelegramPanelAiService
{
    /// <summary>
    /// 根据消息文本、按钮列表与可选图片，决定下一步动作。
    /// </summary>
    Task<TelegramPanelAiChooseActionResult> ChooseActionAsync(
        TelegramPanelAiChooseActionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据题目或上下文生成要发送的文本答案。
    /// </summary>
    Task<TelegramPanelAiReplyTextResult> ReplyTextAsync(
        TelegramPanelAiReplyTextRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TelegramPanelAiButtonOption(int Index, string Text);

public sealed record TelegramPanelAiImageInput(byte[] Content, string MimeType = "image/jpeg");

public sealed record TelegramPanelAiChooseActionRequest(
    string? Model,
    string? MessageText,
    IReadOnlyList<TelegramPanelAiButtonOption> Buttons,
    TelegramPanelAiImageInput? Image,
    string? Context = null);

public sealed record TelegramPanelAiChooseActionResult(
    bool Success,
    string? Mode,
    int? ButtonIndex,
    string? ReplyText,
    string? Reason,
    string? Error);

public sealed record TelegramPanelAiReplyTextRequest(
    string? Model,
    string Prompt,
    string Query,
    TelegramPanelAiImageInput? Image = null,
    string? Context = null);

public sealed record TelegramPanelAiReplyTextResult(
    bool Success,
    string? ReplyText,
    string? Error);
