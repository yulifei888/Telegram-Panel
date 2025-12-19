using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

public static class SessionDataConverter
{
    public readonly record struct SessionConvertResult(bool Ok, string? Reason)
    {
        public static SessionConvertResult Success() => new(true, null);
        public static SessionConvertResult Fail(string? reason) => new(false, string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason.Trim());
    }

    public static async Task<SessionConvertResult> TryConvertSqliteSessionFromJsonAsync(
        string phone,
        int apiId,
        string apiHash,
        string sqliteSessionPath,
        ILogger logger)
    {
        try
        {
            var absoluteSqliteSessionPath = Path.GetFullPath(sqliteSessionPath);
            if (!File.Exists(absoluteSqliteSessionPath) || !LooksLikeSqliteSession(absoluteSqliteSessionPath))
                return SessionConvertResult.Fail("不是有效的 SQLite .session 文件");

            var jsonPath = TryFindAnySessionJsonPath(phone, absoluteSqliteSessionPath);
            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var jsonText = await File.ReadAllTextAsync(jsonPath);
                using var doc = JsonDocument.Parse(jsonText);

                JsonElement sessionProp;
                var hasSessionProp = doc.RootElement.TryGetProperty("session_string", out sessionProp);
                if (!hasSessionProp || sessionProp.ValueKind != JsonValueKind.String)
                    hasSessionProp = doc.RootElement.TryGetProperty("sessionString", out sessionProp);

                if (hasSessionProp && sessionProp.ValueKind == JsonValueKind.String)
                {
                    var sessionString = sessionProp.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionString))
                    {
                        _ = doc.RootElement.TryGetProperty("user_id", out var userIdProp);
                        _ = doc.RootElement.TryGetProperty("uid", out var uidProp);
                        long? userId = null;
                        if (userIdProp.ValueKind == JsonValueKind.Number && userIdProp.TryGetInt64(out var uid1)) userId = uid1;
                        if (userId == null && uidProp.ValueKind == JsonValueKind.Number && uidProp.TryGetInt64(out var uid2)) userId = uid2;

                        var converted = await TryCreateWTelegramSessionFromSessionStringAsync(
                            sessionString: sessionString.Trim(),
                            apiId: apiId,
                            apiHash: apiHash,
                            targetSessionPath: absoluteSqliteSessionPath,
                            phone: phone,
                            userId: userId,
                            logger: logger);

                        if (converted.Ok)
                        {
                            logger.LogInformation("Converted sqlite session for {Phone} using json: {JsonPath}", phone, jsonPath);
                            return SessionConvertResult.Success();
                        }

                        logger.LogWarning("Failed to convert sqlite session for {Phone} using json session_string: {Reason}", phone, converted.Reason);
                    }
                }
            }

            // json 不存在/缺少 session_string/转换失败 → 直接从 sqlite 读取 dc/auth_key 进行转换
            var sqliteConverted = await TryCreateWTelegramSessionFromTelethonSqliteFileAsync(
                sqliteSessionPath: absoluteSqliteSessionPath,
                apiId: apiId,
                apiHash: apiHash,
                targetSessionPath: absoluteSqliteSessionPath,
                phone: phone,
                userId: null,
                logger: logger);

            if (sqliteConverted.Ok)
                logger.LogInformation("Converted sqlite session for {Phone} using sqlite content", phone);
            else
                logger.LogWarning("Failed to convert sqlite session for {Phone} using sqlite content: {Reason}", phone, sqliteConverted.Reason);

            return sqliteConverted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to convert sqlite session for {Phone} from json", phone);
            return SessionConvertResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static async Task<SessionConvertResult> TryCreateWTelegramSessionFromSessionStringAsync(
        string sessionString,
        int apiId,
        string apiHash,
        string targetSessionPath,
        string phone,
        long? userId,
        ILogger logger)
    {
        string? backupPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sessionString))
                return SessionConvertResult.Fail("session_string 为空");

            var absoluteTargetSessionPath = Path.GetFullPath(targetSessionPath);
            var normalizedPhone = NormalizePhone(phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                normalizedPhone = NormalizePhone(Path.GetFileNameWithoutExtension(absoluteTargetSessionPath));
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                return SessionConvertResult.Fail("无法从手机号/文件名解析出 phoneDigits");

            if (!TryParseTelethonStringSession(sessionString.Trim(), out var telethon))
                return SessionConvertResult.Fail("session_string 无法解析为 Telethon StringSession（可能格式不兼容或已损坏）");

            // 先备份旧 sqlite session，再生成 WTelegram session 覆盖原路径
            if (File.Exists(absoluteTargetSessionPath))
            {
                var suffix = LooksLikeSqliteSession(absoluteTargetSessionPath) ? "sqlite.bak" : "bak";
                backupPath = BuildBackupPath(absoluteTargetSessionPath, suffix);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? Directory.GetCurrentDirectory());
                File.Move(absoluteTargetSessionPath, backupPath, overwrite: true);
            }

            var sessionsDir = Path.GetDirectoryName(absoluteTargetSessionPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(sessionsDir);

            // 使用 WTelegram 的 Session 存储格式生成可用 session 文件（加密 JSON）
            var written = await WriteWTelegramSessionFileAsync(
                apiId: apiId,
                apiHash: apiHash,
                sessionPath: absoluteTargetSessionPath,
                phoneDigits: normalizedPhone,
                userId: userId,
                dcId: telethon.DcId,
                ipAddress: telethon.IpAddress,
                port: telethon.Port,
                authKey: telethon.AuthKey,
                logger: logger
            );
            if (!written.Ok)
                return written;

            return SessionConvertResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create WTelegram session from session_string");
            try { if (File.Exists(targetSessionPath)) File.Delete(targetSessionPath); } catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath) && !File.Exists(targetSessionPath))
                    File.Move(backupPath, targetSessionPath, overwrite: true);
            }
            catch { }
            return SessionConvertResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool LooksLikeSqliteSession(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            var read = fs.Read(header);
            if (read < 15) return false;
            var text = Encoding.ASCII.GetString(header[..15]);
            return string.Equals(text, "SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<SessionConvertResult> TryCreateWTelegramSessionFromTelethonSqliteFileAsync(
        string sqliteSessionPath,
        int apiId,
        string apiHash,
        string targetSessionPath,
        string phone,
        long? userId,
        ILogger logger)
    {
        string? backupPath = null;
        try
        {
            var absoluteSqlitePath = Path.GetFullPath(sqliteSessionPath);
            var absoluteTarget = Path.GetFullPath(targetSessionPath);

            if (!File.Exists(absoluteSqlitePath) || !LooksLikeSqliteSession(absoluteSqlitePath))
                return SessionConvertResult.Fail("不是有效的 SQLite .session 文件");

            if (!TryReadTelethonSqliteSession(absoluteSqlitePath, out var telethon, out var readReason))
                return SessionConvertResult.Fail($"SQLite session 读取失败：{readReason}");

            var normalizedPhone = NormalizePhone(phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                normalizedPhone = NormalizePhone(Path.GetFileNameWithoutExtension(absoluteTarget));
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                return SessionConvertResult.Fail("无法从手机号/文件名解析出 phoneDigits");

            if (File.Exists(absoluteTarget))
            {
                var suffix = LooksLikeSqliteSession(absoluteTarget) ? "sqlite.bak" : "bak";
                backupPath = BuildBackupPath(absoluteTarget, suffix);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? Directory.GetCurrentDirectory());
                File.Move(absoluteTarget, backupPath, overwrite: true);
            }

            var written = await WriteWTelegramSessionFileAsync(
                apiId: apiId,
                apiHash: apiHash,
                sessionPath: absoluteTarget,
                phoneDigits: normalizedPhone ?? string.Empty,
                userId: userId,
                dcId: telethon.DcId,
                ipAddress: telethon.IpAddress,
                port: telethon.Port,
                authKey: telethon.AuthKey,
                logger: logger
            );

            if (!written.Ok)
                return written;

            return SessionConvertResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create WTelegram session from telethon sqlite session");
            try { if (File.Exists(targetSessionPath)) File.Delete(targetSessionPath); } catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath) && !File.Exists(targetSessionPath))
                    File.Move(backupPath, targetSessionPath, overwrite: true);
            }
            catch { }
            return SessionConvertResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryReadTelethonSqliteSession(string sqliteSessionPath, out TelethonSessionData data, out string reason)
    {
        try
        {
            data = default;
            reason = "未知原因";
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sqliteSessionPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT dc_id, server_address, port, auth_key FROM sessions LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                reason = "sessions 表为空或无记录";
                return false;
            }

            var dcId = reader.GetInt32(0);
            var serverAddress = reader.IsDBNull(1) ? null : reader.GetString(1);
            var port = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            var authKey = reader.IsDBNull(3) ? null : (byte[])reader[3];

            if (dcId <= 0)
            {
                reason = $"dc_id 无效：{dcId}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                reason = "server_address 为空";
                return false;
            }

            if (port <= 0)
            {
                reason = $"port 无效：{port}";
                return false;
            }

            if (authKey == null || authKey.Length == 0)
            {
                reason = "auth_key 为空";
                return false;
            }

            if (authKey.Length != 256)
            {
                reason = $"auth_key 长度不符合预期：{authKey.Length}（期望 256）";
                return false;
            }

            data = new TelethonSessionData(dcId, serverAddress.Trim(), (ushort)port, authKey);
            reason = "OK";
            return true;
        }
        catch (SqliteException ex)
        {
            data = default;
            reason = $"SqliteException: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            data = default;
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string? TryFindAnySessionJsonPath(string phone, string absoluteSessionPath)
    {
        var normalizedPhone = NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return null;

        // 1) 优先找与 session 同目录的 phone.json（最常见：sessions/<phone>.json）
        var sessionDir = Path.GetDirectoryName(absoluteSessionPath);
        if (!string.IsNullOrWhiteSpace(sessionDir))
        {
            var direct = Path.Combine(sessionDir, $"{normalizedPhone}.json");
            if (File.Exists(direct))
                return direct;

            var anyInSessionDir = Directory.EnumerateFiles(sessionDir, "*.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => string.Equals(NormalizePhone(Path.GetFileNameWithoutExtension(p)), normalizedPhone, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(anyInSessionDir))
                return anyInSessionDir;
        }

        // 2) 尝试在仓库根目录的 session数据/<phone> 下找
        var repoRoot = TryFindRepoRoot();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var sessionDataDir = Path.Combine(repoRoot, "session数据", normalizedPhone);
            var direct = Path.Combine(sessionDataDir, $"{normalizedPhone}.json");
            if (File.Exists(direct))
                return direct;

            if (Directory.Exists(sessionDataDir))
            {
                var any = Directory.EnumerateFiles(sessionDataDir, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(any))
                    return any;
            }

            // 3) 兜底：在 session数据 下递归扫描 phone 字段匹配
            var sessionDataRoot = Path.Combine(repoRoot, "session数据");
            var scanned = TryScanJsonByPhone(sessionDataRoot, normalizedPhone);
            if (!string.IsNullOrWhiteSpace(scanned))
                return scanned;

            // 4) 再兜底：在 sessions 下递归扫描（部分用户会把 json 放在 sessions/ 里）
            var sessionsRoot = Path.Combine(repoRoot, "sessions");
            scanned = TryScanJsonByPhone(sessionsRoot, normalizedPhone);
            if (!string.IsNullOrWhiteSpace(scanned))
                return scanned;
        }

        return null;
    }

    private static byte[] DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        var mod = s.Length % 4;
        if (mod == 2) s += "==";
        else if (mod == 3) s += "=";

        try
        {
            return Convert.FromBase64String(s);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private readonly record struct TelethonSessionData(int DcId, string IpAddress, ushort Port, byte[] AuthKey);

    private static bool TryParseTelethonStringSession(string sessionString, out TelethonSessionData data)
    {
        try
        {
            data = default;
            if (string.IsNullOrWhiteSpace(sessionString) || sessionString.Length < 16)
                return false;

            // Telethon StringSession: first char is version digit
            if (!char.IsDigit(sessionString[0]))
                return false;

            var body = sessionString.Substring(1);
            var packed = DecodeBase64Url(body);

            // 常见 Telethon packed bytes 长度：
            // - IPv4: 263 = 1(dc_id)+4(ip)+2(port)+256(auth_key)
            // - IPv6: 275 = 1+16+2+256
            if (packed.Length is not (263 or 275))
                return false;

            var dcId = packed[0];
            var ipLen = packed.Length - 1 - 2 - 256;
            if (ipLen is not (4 or 16))
                return false;

            var ipBytes = packed.AsSpan(1, ipLen).ToArray();
            var ip = new IPAddress(ipBytes).ToString();
            var port = BinaryPrimitives.ReadUInt16BigEndian(packed.AsSpan(1 + ipLen, 2));
            var authKey = packed.AsSpan(1 + ipLen + 2, 256).ToArray();

            data = new TelethonSessionData(dcId, ip, port, authKey);
            return true;
        }
        catch
        {
            data = default;
            return false;
        }
    }

    private static async Task<SessionConvertResult> WriteWTelegramSessionFileAsync(
        int apiId,
        string apiHash,
        string sessionPath,
        string phoneDigits,
        long? userId,
        int dcId,
        string ipAddress,
        ushort port,
        byte[] authKey,
        ILogger logger)
    {
        string Config(string what) => what switch
        {
            "api_id" => apiId.ToString(),
            "api_hash" => apiHash,
            "session_key" => apiHash,
            "session_pathname" => sessionPath,
            "phone_number" => phoneDigits,
            "user_id" => userId?.ToString() ?? "-1",
            _ => null!
        };

        // 1) 创建空 session 文件
        try
        {
            await using var builder = new Client(Config);
            var clientType = typeof(Client);
            var sessionField = clientType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 WTelegram.Client._session");
            var dcSessionField = clientType.GetField("_dcSession", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 WTelegram.Client._dcSession");

            var sessionObj = sessionField.GetValue(builder) ?? throw new InvalidOperationException("WTelegram session 未初始化");
            var sessionType = sessionObj.GetType();

            var dcSessionType = sessionType.GetNestedType("DCSession", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 WTelegram.Session.DCSession");
            var dcSessionObj = Activator.CreateInstance(dcSessionType) ?? throw new InvalidOperationException("无法创建 DCSession");

            // TL.DcOption 是公开类型，直接构造
            var dcOption = new DcOption { id = dcId, ip_address = ipAddress, port = port, flags = 0 };

            // 填充 DCSession：AuthKey + DataCenter + UserId + authKeyID
            dcSessionType.GetField("AuthKey")?.SetValue(dcSessionObj, authKey);
            dcSessionType.GetField("UserId")?.SetValue(dcSessionObj, userId ?? 0);
            dcSessionType.GetField("DataCenter")?.SetValue(dcSessionObj, dcOption);
            dcSessionType.GetField("Layer")?.SetValue(dcSessionObj, 0);

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(authKey);
                var authKeyId = BinaryPrimitives.ReadInt64LittleEndian(hash.AsSpan(12, 8));
                dcSessionType.GetField("authKeyID", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(dcSessionObj, authKeyId);
            }

            dcSessionType.GetField("Client", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(dcSessionObj, builder);

            // 填充 Session：MainDC + UserId + DcOptions + DCSessions
            sessionType.GetField("MainDC")?.SetValue(sessionObj, dcId);
            sessionType.GetField("UserId")?.SetValue(sessionObj, userId ?? 0);
            sessionType.GetField("DcOptions")?.SetValue(sessionObj, new[] { dcOption });

            var dcSessionsField = sessionType.GetField("DCSessions");
            var dcSessions = dcSessionsField?.GetValue(sessionObj) as System.Collections.IDictionary;
            if (dcSessions == null)
                throw new InvalidOperationException("无法访问 Session.DCSessions");
            dcSessions[dcId] = dcSessionObj;

            // 同步 builder 当前 dcSession 引用
            dcSessionField.SetValue(builder, dcSessionObj);

            // 保存 session（写入加密 JSON）
            var save = sessionType.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 Session.Save()");
            lock (sessionObj) save.Invoke(sessionObj, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write WTelegram session file for {Phone}", phoneDigits);
            return SessionConvertResult.Fail($"WTelegram session 文件写入失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 2) 验证：使用已导入的 AuthKey 直接请求 Self（避免进入 LoginUserIfNeeded 的“发验证码”分支）
        await using var probe = new Client(Config);
        try
        {
            await probe.ConnectAsync();
            var users = await probe.Users_GetUsers(InputUser.Self);
            var self = users.OfType<User>().FirstOrDefault();
            if (self == null)
                return SessionConvertResult.Fail("已连接但无法获取 Self（可能 session 未授权/已失效）");

            // 写回 UserId（让后续服务端能用 LoginUserIfNeeded 快速恢复 User）
            var clientType = typeof(Client);
            var sessionField = clientType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 WTelegram.Client._session");
            var dcSessionField = clientType.GetField("_dcSession", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 WTelegram.Client._dcSession");

            var sessionObj = sessionField.GetValue(probe) ?? throw new InvalidOperationException("WTelegram session 未初始化");
            var sessionType = sessionObj.GetType();
            sessionType.GetField("UserId")?.SetValue(sessionObj, self.id);

            var dcSessionObj = dcSessionField.GetValue(probe);
            dcSessionObj?.GetType().GetField("UserId")?.SetValue(dcSessionObj, self.id);

            var save = sessionType.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("无法访问 Session.Save()");
            lock (sessionObj) save.Invoke(sessionObj, null);

            logger.LogInformation("WTelegram session validated for {Phone} (user_id={UserId}) on DC {DcId}", phoneDigits, self.id, dcId);
            return SessionConvertResult.Success();
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "WTelegram session validation failed for {Phone}", phoneDigits);
            return SessionConvertResult.Fail($"Telegram RPC 错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WTelegram session validation failed for {Phone}", phoneDigits);
            return SessionConvertResult.Fail($"验证失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildBackupPath(string originalPath, string suffix)
    {
        var fullPath = Path.GetFullPath(originalPath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        return Path.Combine(dir, $"{name}.{suffix}{ext}");
    }

    private static string? TryFindRepoRoot()
    {
        var current = Directory.GetCurrentDirectory();
        for (int i = 0; i < 10 && !string.IsNullOrWhiteSpace(current); i++)
        {
            if (File.Exists(Path.Combine(current, "TelegramPanel.sln")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static string? TryScanJsonByPhone(string rootDir, string normalizedPhone)
    {
        try
        {
            if (!Directory.Exists(rootDir))
                return null;

            foreach (var jsonPath in Directory.EnumerateFiles(rootDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(jsonPath);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("phone", out var phoneProp) && phoneProp.ValueKind == JsonValueKind.String)
                    {
                        var p = NormalizePhone(phoneProp.GetString());
                        if (string.Equals(p, normalizedPhone, StringComparison.Ordinal))
                            return jsonPath;
                    }
                }
                catch
                {
                    // ignore single json parse error
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var digits = new StringBuilder(phone.Length);
        foreach (var ch in phone)
        {
            if (ch >= '0' && ch <= '9')
                digits.Append(ch);
        }
        return digits.ToString();
    }

    // 备份逻辑在 TryCreateWTelegramSessionFromSessionStringAsync 内集中处理
}
