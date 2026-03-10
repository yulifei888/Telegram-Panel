using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

public enum AccountExportFormat
{
    Telethon = 0,
    Tdata = 1
}

public class AccountExportService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountExportService> _logger;
    private readonly ITelegramClientPool _clientPool;

    public AccountExportService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<AccountExportService> logger,
        ITelegramClientPool clientPool)
    {
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _clientPool = clientPool;
    }

    public async Task<byte[]> BuildAccountsZipAsync(
        IReadOnlyList<Account> accounts,
        CancellationToken cancellationToken,
        AccountExportFormat format = AccountExportFormat.Telethon)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteReadmeAsync(zip, format);

            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var phone = (account.Phone ?? string.Empty).Trim();
                var safeFolder = BuildSafeFolderName(phone, account.Id);

                _ = zip.CreateEntry($"{safeFolder}/");

                try
                {
                    var sessionPath = ResolveSessionPath(account);
                    var hasSession = false;
                    if (!File.Exists(sessionPath))
                    {
                        _logger.LogWarning("Session file missing for account {AccountId}: {Path}", account.Id, sessionPath);
                        await WriteTextEntryAsync(zip, $"{safeFolder}/WARN.txt", $"未找到 session 文件：{sessionPath}");
                    }
                    else
                    {
                        await CopySessionWithRetryAsync(zip, safeFolder, sessionPath, account.Id, cancellationToken);
                        hasSession = true;
                    }

                    await WriteAccountJsonAsync(zip, safeFolder, account, phone);

                    var twoFactorPassword = (account.TwoFactorPassword ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(twoFactorPassword))
                        await WriteTextEntryAsync(zip, $"{safeFolder}/2fa.txt", twoFactorPassword);

                    if (format == AccountExportFormat.Tdata)
                    {
                        if (!hasSession)
                        {
                            await WriteTextEntryAsync(zip, $"{safeFolder}/WARN_TDATA.txt", "无法生成 tdata：session 文件不存在");
                        }
                        else
                        {
                            var tdataResult = await TryAddTdataPackageAsync(zip, safeFolder, account, sessionPath, cancellationToken);
                            if (!tdataResult.Ok)
                                await WriteTextEntryAsync(zip, $"{safeFolder}/WARN_TDATA.txt", $"tdata 生成失败：{tdataResult.Error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to export account {AccountId}", account.Id);
                    await WriteTextEntryAsync(zip, $"{safeFolder}/ERROR.txt", $"导出该账号时发生异常：{ex.Message}");
                }
            }
        }

        return ms.ToArray();
    }

    private async Task WriteReadmeAsync(ZipArchive zip, AccountExportFormat format)
    {
        var readme = zip.CreateEntry("README.txt", CompressionLevel.Fastest);
        await using var readmeStream = readme.Open();
        await using var writer = new StreamWriter(readmeStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await writer.WriteLineAsync("Telegram Panel 账号导出包");
        await writer.WriteLineAsync($"导出时间(UTC)：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"导出格式：{(format == AccountExportFormat.Tdata ? "tdata（包含 tdata + .json + .session）" : "telethon（.json + .session）")}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("通用结构：每个账号一个子文件夹，至少包含 .json + .session；如保存了二级密码，则额外包含 2fa.txt。");
        if (format == AccountExportFormat.Tdata)
            await writer.WriteLineAsync("tdata 结构：每个账号目录下额外包含 tdata/ 子目录（key_datas / D877F783D5D3EF8C*）。");
        await writer.WriteLineAsync("导入：面板 -> 账号 -> 导入账号 -> 压缩包导入（Zip）。");
    }

    private async Task WriteAccountJsonAsync(ZipArchive zip, string safeFolder, Account account, string phone)
    {
        var jsonEntry = zip.CreateEntry($"{safeFolder}/{safeFolder}.json", CompressionLevel.Fastest);
        await using var jsonStream = jsonEntry.Open();
        await using var writer = new StreamWriter(jsonStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            phone = phone,
            user_id = account.UserId > 0 ? (long?)account.UserId : null,
            username = account.Username,
            first_name = account.Nickname,
            api_id = account.ApiId,
            api_hash = account.ApiHash,
            exported_at_utc = DateTime.UtcNow
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        await writer.WriteAsync(json);
    }

    private readonly record struct TdataExportResult(bool Ok, string? Error)
    {
        public static TdataExportResult Success() => new(true, null);
        public static TdataExportResult Fail(string? error) => new(false, string.IsNullOrWhiteSpace(error) ? "未知原因" : error.Trim());
    }

    private async Task<TdataExportResult> TryAddTdataPackageAsync(
        ZipArchive zip,
        string safeFolder,
        Account account,
        string sourceSessionPath,
        CancellationToken cancellationToken)
    {
        string? tempSessionPath = null;
        string? tempTdataDir = null;

        try
        {
            if (account.ApiId <= 0 || string.IsNullOrWhiteSpace(account.ApiHash))
                return TdataExportResult.Fail("账号缺少 ApiId 或 ApiHash");

            tempSessionPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-export-{Guid.NewGuid():N}.session");
            tempTdataDir = Path.Combine(Path.GetTempPath(), $"telegram-panel-export-tdata-{Guid.NewGuid():N}");

            // 若 session 被当前进程锁定，先释放客户端后重试复制
            await CopySessionFileToPathWithRetryAsync(
                sourceSessionPath: sourceSessionPath,
                targetSessionPath: tempSessionPath,
                accountId: account.Id,
                cancellationToken: cancellationToken);

            var telethonResult = SessionDataConverter.TryCreateTelethonStringSessionFromWTelegramSessionFile(
                sessionPath: tempSessionPath,
                apiId: account.ApiId,
                apiHash: account.ApiHash.Trim(),
                phone: account.Phone,
                userId: account.UserId > 0 ? account.UserId : null,
                logger: _logger);
            if (!telethonResult.Ok || string.IsNullOrWhiteSpace(telethonResult.SessionString))
                return TdataExportResult.Fail($"生成 Telethon session_string 失败：{telethonResult.Reason}");

            var tdataResult = await TdataSessionBridge.TryConvertTelethonStringSessionToTdataAsync(
                telethonSessionString: telethonResult.SessionString,
                userId: account.UserId > 0 ? account.UserId : null,
                outputTdataDirectory: tempTdataDir,
                logger: _logger,
                cancellationToken: cancellationToken);
            if (!tdataResult.Ok)
                return TdataExportResult.Fail(tdataResult.Error);

            await AddDirectoryToZipAsync(
                zip: zip,
                sourceDirectory: tempTdataDir,
                zipDirectoryPrefix: $"{safeFolder}/tdata",
                cancellationToken: cancellationToken);

            return TdataExportResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build tdata package for account {AccountId}", account.Id);
            return TdataExportResult.Fail(ex.Message);
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tempSessionPath) && File.Exists(tempSessionPath))
                    File.Delete(tempSessionPath);
            }
            catch
            {
                // ignore
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(tempTdataDir) && Directory.Exists(tempTdataDir))
                    Directory.Delete(tempTdataDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task CopySessionFileToPathWithRetryAsync(
        string sourceSessionPath,
        string targetSessionPath,
        int accountId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Directory.CreateDirectory(Path.GetDirectoryName(targetSessionPath) ?? Path.GetTempPath());

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var source = new FileStream(sourceSessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var target = new FileStream(targetSessionPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, cancellationToken);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Session file is locked while preparing tdata export (attempt {Attempt}/{Max}) for account {AccountId}: {Path}",
                    attempt, maxAttempts, accountId, sourceSessionPath);
                try
                {
                    await _clientPool.RemoveClientAsync(accountId);
                }
                catch (Exception removeEx)
                {
                    _logger.LogDebug(removeEx, "Failed to remove client for account {AccountId} while preparing tdata export", accountId);
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        throw new IOException($"session 文件被占用无法读取：{sourceSessionPath}");
    }

    private static async Task AddDirectoryToZipAsync(
        ZipArchive zip,
        string sourceDirectory,
        string zipDirectoryPrefix,
        CancellationToken cancellationToken)
    {
        var normalizedPrefix = zipDirectoryPrefix.TrimEnd('/').Replace('\\', '/');
        _ = zip.CreateEntry($"{normalizedPrefix}/");

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            var entryPath = $"{normalizedPrefix}/{relativePath}";
            var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);

            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await fileStream.CopyToAsync(entryStream, cancellationToken);
        }
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content ?? string.Empty);
    }

    private async Task CopySessionWithRetryAsync(
        ZipArchive zip,
        string safeFolder,
        string sessionPath,
        int accountId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = zip.CreateEntry($"{safeFolder}/{safeFolder}.session", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var fileStream = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await fileStream.CopyToAsync(entryStream, cancellationToken);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Session file is locked (attempt {Attempt}/{Max}) for account {AccountId}: {Path}", attempt, maxAttempts, accountId, sessionPath);

                try
                {
                    await _clientPool.RemoveClientAsync(accountId);
                }
                catch (Exception removeEx)
                {
                    _logger.LogDebug(removeEx, "Failed to remove client for account {AccountId} while exporting", accountId);
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        await WriteTextEntryAsync(zip, $"{safeFolder}/WARN.txt", $"session 文件被占用无法读取：{sessionPath}\n建议：先在面板停止该账号的 Telegram 客户端（或重启应用）后再导出。");
    }

    private string ResolveSessionPath(Account account)
    {
        var sessionPath = account.SessionPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            var phoneDigits = NormalizePhone(account.Phone);
            return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, sessionsPath, $"{phoneDigits}.session"));
        }

        if (Path.IsPathRooted(sessionPath))
            return Path.GetFullPath(sessionPath);

        var combined = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, sessionPath));
        if (File.Exists(combined))
            return combined;

        return Path.GetFullPath(sessionPath);
    }

    private static string BuildSafeFolderName(string phone, int accountId)
    {
        var digits = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(digits))
            return digits;
        return $"account_{accountId}";
    }

    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var digits = new char[phone.Length];
        var count = 0;
        foreach (var ch in phone)
        {
            if (ch >= '0' && ch <= '9')
                digits[count++] = ch;
        }
        return count == 0 ? string.Empty : new string(digits, 0, count);
    }
}
