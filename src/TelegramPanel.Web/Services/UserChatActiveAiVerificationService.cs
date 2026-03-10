using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class UserChatActiveAiVerificationService
{
    private readonly AccountTelegramToolsService _accountTools;
    private readonly ITelegramPanelAiService _aiService;
    private readonly ILogger<UserChatActiveAiVerificationService> _logger;

    public UserChatActiveAiVerificationService(
        AccountTelegramToolsService accountTools,
        ITelegramPanelAiService aiService,
        ILogger<UserChatActiveAiVerificationService> logger)
    {
        _accountTools = accountTools;
        _aiService = aiService;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error, string? ActionSummary)> TryHandleAsync(
        Account account,
        AccountTelegramToolsService.ResolvedChatTarget target,
        int sentMessageId,
        UserChatActiveTaskConfig config,
        CancellationToken cancellationToken)
    {
        var wait = await _accountTools.WaitForBotVerificationMessageAsync(
            account.Id,
            target,
            sentMessageId,
            account.Username,
            config.VerificationTimeoutSeconds,
            cancellationToken);

        if (!wait.Success || wait.Candidate == null)
            return (false, wait.Error, null);

        var candidate = wait.Candidate;
        var image = candidate.ImageJpegBytes is { Length: > 0 }
            ? new TelegramPanelAiImageInput(candidate.ImageJpegBytes)
            : null;

        if (candidate.Buttons.Count > 0)
        {
            var decision = await _aiService.ChooseActionAsync(
                new TelegramPanelAiChooseActionRequest(
                    Model: config.AiModel,
                    MessageText: candidate.Text,
                    Buttons: candidate.Buttons.Select(x => new TelegramPanelAiButtonOption(x.Index, x.Text)).ToList(),
                    Image: image,
                    Context: "这是 Telegram 群里的验证消息，请根据按钮文案、题目文本和图片决定最合适动作。"),
                cancellationToken);

            if (!decision.Success)
                return (false, decision.Error, null);

            if (string.Equals(decision.Mode, "reply_text", StringComparison.OrdinalIgnoreCase))
            {
                var replyText = (decision.ReplyText ?? string.Empty).Trim();
                if (replyText.Length == 0)
                    return (false, "AI 返回了 reply_text，但内容为空", null);

                var mappedButton = TryMatchButtonByReplyText(candidate.Buttons, replyText);
                if (mappedButton != null)
                {
                    var mappedClick = await _accountTools.ClickInlineButtonAsync(
                        account.Id,
                        target,
                        candidate.MessageId,
                        mappedButton.CallbackData,
                        cancellationToken);

                    if (!mappedClick.Success)
                        return (false, mappedClick.Error, null);

                    return (true, null, $"AI 文本映射按钮：{mappedButton.Text}");
                }

                return (false, $"验证码消息存在按钮，但 AI 返回了文本：{replyText}", null);
            }

            var button = candidate.Buttons.FirstOrDefault(x => x.Index == decision.ButtonIndex);
            if (button == null)
                return (false, $"AI 返回的按钮索引无效：{decision.ButtonIndex}", null);

            var click = await _accountTools.ClickInlineButtonAsync(
                account.Id,
                target,
                candidate.MessageId,
                button.CallbackData,
                cancellationToken);

            if (!click.Success)
                return (false, click.Error, null);

            _logger.LogInformation(
                "AI verification button clicked for account {AccountId}, chat {ChatId}, message {MessageId}",
                account.Id,
                target.CanonicalId,
                candidate.MessageId);

            return (true, null, $"AI 点击按钮：{button.Text}");
        }

        var replyDecision = await _aiService.ReplyTextAsync(
            new TelegramPanelAiReplyTextRequest(
                Model: config.AiModel,
                Prompt: "这是 Telegram 群里的验证消息，请直接给出最终答案文本。",
                Query: candidate.Text ?? string.Empty,
                Image: image,
                Context: "只返回最终答案，不要解释。"),
            cancellationToken);

        if (!replyDecision.Success)
            return (false, replyDecision.Error, null);

        var replyContent = (replyDecision.ReplyText ?? string.Empty).Trim();
        if (replyContent.Length == 0)
            return (false, "AI 返回答案为空", null);

        var sendReply = await _accountTools.SendMessageToResolvedChatAsync(
            account.Id,
            target,
            replyContent,
            replyToMessageId: candidate.MessageId,
            cancellationToken: cancellationToken);

        if (!sendReply.Success)
            return (false, sendReply.Error, null);

        _logger.LogInformation(
            "AI verification reply sent for account {AccountId}, chat {ChatId}, message {MessageId}",
            account.Id,
            target.CanonicalId,
            candidate.MessageId);

        return (true, null, $"AI 文本回复：{replyContent}");
    }

    private static TelegramInlineButtonOption? TryMatchButtonByReplyText(
        IReadOnlyList<TelegramInlineButtonOption> buttons,
        string replyText)
    {
        var normalizedReply = NormalizeButtonText(replyText);
        if (normalizedReply.Length == 0)
            return null;

        foreach (var button in buttons)
        {
            if (NormalizeButtonText(button.Text) == normalizedReply)
                return button;
        }

        foreach (var button in buttons)
        {
            var normalizedButton = NormalizeButtonText(button.Text);
            if (normalizedButton.Contains(normalizedReply, StringComparison.OrdinalIgnoreCase)
                || normalizedReply.Contains(normalizedButton, StringComparison.OrdinalIgnoreCase))
            {
                return button;
            }
        }

        return null;
    }

    private static string NormalizeButtonText(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
