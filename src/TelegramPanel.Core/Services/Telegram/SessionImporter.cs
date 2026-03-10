using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Interfaces;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Session导入服务实现
/// </summary>
public class SessionImporter : ISessionImporter
{
    private readonly ILogger<SessionImporter> _logger;
    private readonly IConfiguration _configuration;

    public SessionImporter(IConfiguration configuration, ILogger<SessionImporter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ImportResult> ImportFromSessionFileAsync(
        string filePath,
        int apiId,
        string apiHash,
        long? userId = null,
        string? phoneHint = null,
        string? sessionKey = null)
    {
        if (!File.Exists(filePath))
        {
            return new ImportResult(false, null, null, null, null, $"Session file not found: {filePath}");
        }

        try
        {
            _logger.LogInformation("Importing session from file: {FilePath}", filePath);

            // 一些来源的 .session 实际上是 SQLite 格式（常见于 Telegram Desktop 导出）
            // WTelegramClient 无法直接使用这类 session，用户应使用带 json 的压缩包导入或重新登录生成新 session。
            if (LooksLikeSqliteSession(filePath))
            {
                return new ImportResult(
                    false,
                    phoneHint,
                    userId,
                    null,
                    null,
                    "该 .session 为 SQLite 格式（通常来自 Telegram Desktop），本项目不支持直接导入单个 .session；请使用包含 .json + .session 的压缩包导入，或重新登录生成新的 session。");
            }

            // 复制到sessions目录
            var fileName = Path.GetFileName(filePath);
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var targetPath = Path.Combine(sessionsPath, fileName);

            File.Copy(filePath, targetPath, overwrite: true);

            // 使用 config 回调设置 session 路径
            string Config(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => targetPath,
                "session_key" => string.IsNullOrWhiteSpace(sessionKey) ? null! : sessionKey,
                _ => null!
            };

            using var client = new Client(Config);
            await client.ConnectAsync();

            var self = client.User;
            if (self == null)
            {
                try
                {
                    var users = await client.Users_GetUsers(InputUser.Self);
                    self = users.OfType<User>().FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch self user after session connect: {SessionPath}", targetPath);
                }
            }

            if (self != null)
            {
                _logger.LogInformation("Session imported successfully for user {UserId}", self.id);

                return new ImportResult(
                    Success: true,
                    Phone: self.phone,
                    UserId: self.id,
                    Username: self.MainUsername,
                    SessionPath: targetPath
                );
            }

            return new ImportResult(false, null, null, null, targetPath, "Session exists but user not logged in");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import session from {FilePath}", filePath);
            return new ImportResult(false, null, null, null, null, ex.Message);
        }
    }

    public async Task<List<ImportResult>> BatchImportSessionFilesAsync(string[] filePaths, int apiId, string apiHash)
    {
        var results = new List<ImportResult>();

        foreach (var filePath in filePaths)
        {
            var result = await ImportFromSessionFileAsync(filePath, apiId, apiHash);
            results.Add(result);

            // 短暂延迟避免频繁连接
            await Task.Delay(500);
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Batch import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    public async Task<ImportResult> ImportFromStringSessionAsync(string sessionString, int apiId, string apiHash)
    {
        try
        {
            // WTelegramClient 使用二进制session文件，不直接支持StringSession
            // 需要将base64字符串解码并保存为文件

            var sessionData = Convert.FromBase64String(sessionString);
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var sessionPath = Path.Combine(sessionsPath, $"{Guid.NewGuid()}.session");

            await File.WriteAllBytesAsync(sessionPath, sessionData);

            // 使用 config 回调设置 session 路径
            string Config(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => sessionPath,
                _ => null!
            };

            using var client = new Client(Config);
            await client.ConnectAsync();

            var self = client.User;
            if (self == null)
            {
                try
                {
                    var users = await client.Users_GetUsers(InputUser.Self);
                    self = users.OfType<User>().FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch self user after string session connect: {SessionPath}", sessionPath);
                }
            }

            if (self != null)
            {
                // 重命名为手机号
                var newPath = Path.Combine(sessionsPath, $"{self.phone}.session");
                File.Move(sessionPath, newPath, overwrite: true);

                return new ImportResult(
                    Success: true,
                    Phone: self.phone,
                    UserId: self.id,
                    Username: self.MainUsername,
                    SessionPath: newPath
                );
            }

            // 删除无效session
            File.Delete(sessionPath);
            return new ImportResult(false, null, null, null, null, "Invalid session string");
        }
        catch (FormatException)
        {
            return new ImportResult(false, null, null, null, null, "Invalid base64 format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from string session");
            return new ImportResult(false, null, null, null, null, ex.Message);
        }
    }

    public Task<bool> ValidateSessionAsync(string sessionPath)
    {
        if (!File.Exists(sessionPath))
        {
            return Task.FromResult(false);
        }

        try
        {
            // 简单检查文件大小（有效session通常大于0字节）
            var fileInfo = new FileInfo(sessionPath);
            return Task.FromResult(fileInfo.Length > 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static bool LooksLikeSqliteSession(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            var read = fs.Read(header);
            if (read < 15) return false;
            var text = System.Text.Encoding.ASCII.GetString(header[..15]);
            return string.Equals(text, "SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
