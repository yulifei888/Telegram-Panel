using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Telegram客户端池管理
/// 负责管理多个Telegram账号的客户端实例
/// </summary>
public class TelegramClientPool : ITelegramClientPool, IDisposable
{
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramClientPool> _logger;
    private bool _disposed;
    private static int _wtelegramLogConfigured;

    public TelegramClientPool(IConfiguration configuration, ILogger<TelegramClientPool> logger)
    {
        _configuration = configuration;
        _logger = logger;
        ConfigureWTelegramLoggingOnce();
    }

    public int ActiveClientCount => _clients.Count;

    public async Task<Client> GetOrCreateClientAsync(
        int accountId,
        int apiId,
        string apiHash,
        string sessionPath,
        string? sessionKey = null,
        string? phoneNumber = null,
        long? userId = null)
    {
        if (_clients.TryGetValue(accountId, out var existingClient))
        {
            if (existingClient.User != null)
            {
                return existingClient;
            }
            // 客户端存在但未登录，移除并重新创建
            await RemoveClientAsync(accountId);
        }

        var lockObj = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync();

        try
        {
            // 双重检查
            if (_clients.TryGetValue(accountId, out existingClient) && existingClient.User != null)
            {
                return existingClient;
            }

            _logger.LogInformation("Creating new Telegram client for account {AccountId}", accountId);

            // 使用 config 回调设置 session 路径
            phoneNumber = NormalizePhone(phoneNumber);
            string Config(string what)
            {
                var proxyServer = (_configuration["Telegram:Proxy:Server"] ?? "").Trim();
                var proxyPort = (_configuration["Telegram:Proxy:Port"] ?? "").Trim();
                var proxyUsername = (_configuration["Telegram:Proxy:Username"] ?? "").Trim();
                var proxyPassword = (_configuration["Telegram:Proxy:Password"] ?? "").Trim();
                var proxySecret = (_configuration["Telegram:Proxy:Secret"] ?? "").Trim();

                return what switch
                {
                    "api_id" => apiId.ToString(),
                    "api_hash" => apiHash,
                    "session_pathname" => sessionPath,
                    "session_key" => string.IsNullOrWhiteSpace(sessionKey) ? null! : sessionKey,
                    "phone_number" => string.IsNullOrWhiteSpace(phoneNumber) ? null! : phoneNumber,
                    "user_id" => userId.HasValue && userId.Value > 0 ? userId.Value.ToString() : null!,

                    // 代理（可选）：用于 Telegram MTProto 连接（WTelegramClient）。
                    // 常见场景：宿主机能直连 Telegram，但 Docker/服务器环境需要代理。
                    // - SOCKS5/HTTP：配置 server/port；如需认证再填 username/password
                    // - MTProto：额外配置 secret
                    "proxy_server" => string.IsNullOrWhiteSpace(proxyServer) ? null! : proxyServer,
                    "proxy_port" => string.IsNullOrWhiteSpace(proxyPort) ? null! : proxyPort,
                    "proxy_username" => string.IsNullOrWhiteSpace(proxyUsername) ? null! : proxyUsername,
                    "proxy_password" => string.IsNullOrWhiteSpace(proxyPassword) ? null! : proxyPassword,
                    "proxy_secret" => string.IsNullOrWhiteSpace(proxySecret) ? null! : proxySecret,

                    _ => null!  // 使用 null! 抑制警告，这是 WTelegramClient 的预期行为
                };
            }

            var client = new Client(Config);
            TryRebindApiIdForLoadedSession(client, apiId);

            // 设置日志回调
            client.OnOther += (update) =>
            {
                _logger.LogDebug("Account {AccountId} received update: {Update}", accountId, update.GetType().Name);
                return Task.CompletedTask;
            };

            _clients[accountId] = client;
            return client;
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task RemoveAllClientsAsync()
    {
        var accountIds = _clients.Keys.ToArray();
        foreach (var accountId in accountIds)
        {
            await RemoveClientAsync(accountId);
        }
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new char[phone.Length];
        var count = 0;
        foreach (var ch in phone)
        {
            if (ch >= '0' && ch <= '9')
                digits[count++] = ch;
        }
        return count == 0 ? null : new string(digits, 0, count);
    }

    public Client? GetClient(int accountId)
    {
        return _clients.TryGetValue(accountId, out var client) ? client : null;
    }

    public async Task RemoveClientAsync(int accountId)
    {
        if (_clients.TryRemove(accountId, out var client))
        {
            _logger.LogInformation("Removing Telegram client for account {AccountId}", accountId);

            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing client for account {AccountId}", accountId);
            }
        }
    }

    public bool IsClientConnected(int accountId)
    {
        return _clients.TryGetValue(accountId, out var client) && client.User != null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略清理时的错误
            }
        }

        _clients.Clear();

        foreach (var lockObj in _locks.Values)
        {
            lockObj.Dispose();
        }

        _locks.Clear();

        GC.SuppressFinalize(this);
    }

    private void TryRebindApiIdForLoadedSession(Client client, int desiredApiId)
    {
        try
        {
            var clientType = typeof(Client);
            var sessionField = clientType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
            var sessionObj = sessionField?.GetValue(client);
            if (sessionObj == null)
                return;

            var sessionType = sessionObj.GetType();

            int? currentApiId = null;
            var apiIdField = sessionType.GetField("ApiId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (apiIdField?.FieldType == typeof(int))
                currentApiId = (int)apiIdField.GetValue(sessionObj)!;

            var apiIdProp = sessionType.GetProperty("ApiId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (currentApiId == null && apiIdProp?.PropertyType == typeof(int) && apiIdProp.CanRead)
                currentApiId = (int)apiIdProp.GetValue(sessionObj)!;

            if (currentApiId.HasValue && currentApiId.Value == desiredApiId)
                return;

            var updated = false;
            if (apiIdField?.FieldType == typeof(int))
            {
                apiIdField.SetValue(sessionObj, desiredApiId);
                updated = true;
            }
            else if (apiIdProp?.PropertyType == typeof(int) && apiIdProp.CanWrite)
            {
                apiIdProp.SetValue(sessionObj, desiredApiId);
                updated = true;
            }

            var clientApiIdField = clientType.GetField("_api_id", BindingFlags.Instance | BindingFlags.NonPublic);
            if (clientApiIdField?.FieldType == typeof(int))
            {
                clientApiIdField.SetValue(client, desiredApiId);
                updated = true;
            }

            if (!updated)
                return;

            var save = sessionType.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic);
            if (save != null)
            {
                lock (sessionObj) save.Invoke(sessionObj, null);
            }

            _logger.LogInformation(
                "Rebound WTelegram session ApiId from {OldApiId} to {NewApiId} to apply new global Telegram API config",
                currentApiId,
                desiredApiId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to rebind WTelegram session ApiId (ignored)");
        }
    }

    private void ConfigureWTelegramLoggingOnce()
    {
        // WTelegramClient 默认会把大量底层网络/RPC trace 直接写到 Console，严重干扰面板日志查看。
        // 这里把它重定向到宿主 ILogger，以便用 Serilog 的 MinimumLevel/Override 控制输出量。
        if (Interlocked.Exchange(ref _wtelegramLogConfigured, 1) == 1)
            return;

        Helpers.Log = (level, message) =>
        {
            message = (message ?? "").TrimEnd();
            if (message.Length == 0)
                return;

            // 说明：用户通常只关心“限流/错误”等关键信息；
            // 像 MsgsAck / GetDialogs / RpcResult 这类底层 trace 即使在 WTelegram 的低 level 下也会非常多，
            // 因此默认全部降为 Debug，仅将关键错误/限流提升到 Warning/Error，方便在面板里用 Warning 级别过滤出重点。

            if (message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)
                || message.Contains("RpcError", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("WTelegram({Level}): {Message}", level, message);
                return;
            }

            _logger.LogDebug("WTelegram({Level}): {Message}", level, message);
        };
    }
}
