using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class TelegramEmailCodeService : ITelegramEmailCodeService
{
    private readonly IConfiguration _configuration;
    private readonly CloudMailClient _cloudMail;

    public TelegramEmailCodeService(IConfiguration configuration, CloudMailClient cloudMail)
    {
        _configuration = configuration;
        _cloudMail = cloudMail;
    }

    public string BuildEmailByPhoneDigits(string phoneDigits)
    {
        phoneDigits = NormalizeDigits(phoneDigits);
        var domain = ((_configuration["CloudMail:Domain"] ?? string.Empty).Trim()).TrimStart('@');
        if (string.IsNullOrWhiteSpace(domain))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(phoneDigits))
            return string.Empty;
        return $"{phoneDigits}@{domain}";
    }

    public Task<TelegramEmailCodeResult> TryGetLatestCodeByPhoneDigitsAsync(
        string phoneDigits,
        DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default)
    {
        var email = BuildEmailByPhoneDigits(phoneDigits);
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult(new TelegramEmailCodeResult(
                Success: false,
                Code: null,
                Error: "无法生成邮箱地址（请检查 CloudMail:Domain 配置与手机号）",
                ToEmail: null,
                Subject: null,
                CreatedUtc: null));
        }

        return TryGetLatestCodeByEmailAsync(email, sinceUtc, cancellationToken);
    }

    public async Task<TelegramEmailCodeResult> TryGetLatestCodeByEmailAsync(
        string toEmail,
        DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = (_configuration["CloudMail:BaseUrl"] ?? string.Empty).Trim();
        var token = (_configuration["CloudMail:Token"] ?? string.Empty).Trim();
        toEmail = (toEmail ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return new TelegramEmailCodeResult(
                Success: false,
                Code: null,
                Error: "未配置 Cloud Mail（CloudMail:BaseUrl/CloudMail:Token）",
                ToEmail: toEmail,
                Subject: null,
                CreatedUtc: null);
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            return new TelegramEmailCodeResult(
                Success: false,
                Code: null,
                Error: "toEmail 不能为空",
                ToEmail: null,
                Subject: null,
                CreatedUtc: null);
        }

        var allowOldBeforeUtc = sinceUtc ?? DateTimeOffset.UtcNow.AddHours(-1);

        try
        {
            // 尽量宽松：不限制 type/isDel，避免服务端筛选差异导致拿不到邮件
            var req = new CloudMailEmailListRequest
            {
                ToEmail = toEmail,
                TimeSort = "desc",
                Type = null,
                IsDel = null,
                Num = 1,
                Size = 50
            };

            var emails = await _cloudMail.GetEmailListAsync(baseUrl, token, req, cancellationToken);
            if (emails.Count == 0)
            {
                emails = await _cloudMail.GetEmailListAsync(
                    baseUrl,
                    token,
                    req with { ToEmail = "%" + toEmail + "%" },
                    cancellationToken);
            }

            if (emails.Count == 0)
            {
                // 兜底：拉取最近邮件，本地按收件人匹配
                var fallback = await _cloudMail.GetEmailListAsync(
                    baseUrl,
                    token,
                    req with { ToEmail = null },
                    cancellationToken);
                emails = fallback.Where(m => IsRecipientMatch(m.ToEmail, toEmail)).ToList();
            }

            foreach (var mail in emails)
            {
                var createdUtc = TryParseUtc(mail.CreateTime);
                if (createdUtc.HasValue && createdUtc.Value < allowOldBeforeUtc)
                    continue;

                if (!IsRecipientMatch(mail.ToEmail, toEmail))
                    continue;

                var code = TryExtractTelegramCode(mail);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                return new TelegramEmailCodeResult(
                    Success: true,
                    Code: code,
                    Error: null,
                    ToEmail: (mail.ToEmail ?? toEmail).Trim(),
                    Subject: (mail.Subject ?? string.Empty).Trim(),
                    CreatedUtc: createdUtc);
            }

            return new TelegramEmailCodeResult(
                Success: false,
                Code: null,
                Error: "未找到可用验证码（可能是邮件延迟/筛选条件不匹配/验证码已过期）",
                ToEmail: toEmail,
                Subject: null,
                CreatedUtc: null);
        }
        catch (Exception ex)
        {
            return new TelegramEmailCodeResult(
                Success: false,
                Code: null,
                Error: ex.Message,
                ToEmail: toEmail,
                Subject: null,
                CreatedUtc: null);
        }
    }

    private static string NormalizeDigits(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var digits = new char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (ch >= '0' && ch <= '9')
                digits[count++] = ch;
        }
        return count == 0 ? string.Empty : new string(digits, 0, count);
    }

    private static bool IsRecipientMatch(string? candidate, string target)
    {
        target = (target ?? string.Empty).Trim();
        candidate = (candidate ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(candidate))
            return false;

        return string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)
               || candidate.EndsWith(target, StringComparison.OrdinalIgnoreCase)
               || candidate.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? TryParseUtc(string? createTime)
    {
        createTime = (createTime ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(createTime))
            return null;

        if (DateTime.TryParse(createTime, out var dt))
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            else
                dt = dt.ToUniversalTime();

            return new DateTimeOffset(dt);
        }

        return null;
    }

    private static string? TryExtractTelegramCode(CloudMailEmail mail)
    {
        var subject = mail.Subject ?? "";
        var text = mail.Text ?? "";
        var html = mail.Content ?? "";
        var merged = $"{subject}\n{text}\n{StripHtml(html)}";

        // 优先匹配 Telegram 常见模板（尽量减少误判）
        var m = Regex.Match(merged, @"(?i)\byour\s+code\s+is\s*[:：]?\s*(\d{5,6})\b");
        if (m.Success) return m.Groups[1].Value;

        // 兼容部分邮件模板：Login code: 12345
        m = Regex.Match(merged, @"(?i)\blogin\s+code\s*[:：]?\s*(\d{5,6})\b");
        if (m.Success) return m.Groups[1].Value;

        // 匹配带破折号的格式 "your code - 12345"
        m = Regex.Match(merged, @"(?i)\byour\s+code\s*[-–—]\s*(\d{5,6})\b");
        if (m.Success) return m.Groups[1].Value;

        var matches = Regex.Matches(merged, "\\b\\d{5,6}\\b");
        if (matches.Count == 0)
            return null;

        return matches[^1].Value;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        return Regex.Replace(html, "<.*?>", " ").Replace("&nbsp;", " ").Trim();
    }
}
