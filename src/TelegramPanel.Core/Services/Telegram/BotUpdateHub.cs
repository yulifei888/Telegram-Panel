using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Bot API 更新分发 Hub：
/// - 同一个 Bot Token 只允许一个 getUpdates 长轮询，否则会 409 Conflict
/// - 这里为每个 botId 维护一个单一轮询器，并把同一份 updates 广播给多个消费者（模块/后台服务）
/// - 支持 Webhook 模式：外部调用 InjectWebhookUpdateAsync 注入更新
/// </summary>
public sealed class BotUpdateHub : IAsyncDisposable
{
    // 固定允许的更新类型：覆盖当前项目使用场景（转发/监听/入群事件）
    public const string AllowedUpdatesJson = "[\"message\",\"edited_message\",\"channel_post\",\"edited_channel_post\",\"my_chat_member\",\"chat_member\",\"chat_join_request\"]";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramBotApiClient _botApi;
    private readonly ILogger<BotUpdateHub> _logger;
    private readonly bool _webhookEnabled;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BotPoller> _pollersByToken = new(StringComparer.Ordinal);

    // Webhook 模式下的接收器（token -> receiver），不启动轮询
    private readonly ConcurrentDictionary<string, BotWebhookReceiver> _webhookReceivers = new(StringComparer.Ordinal);

    private static readonly TimeSpan WebhookTokenCacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _webhookTokenCacheGate = new(1, 1);
    private DateTimeOffset _webhookTokenCacheBuiltAtUtc = DateTimeOffset.MinValue;
    private Dictionary<string, string> _botTokenByWebhookPathToken = new(StringComparer.Ordinal);

    public BotUpdateHub(
        IServiceScopeFactory scopeFactory,
        TelegramBotApiClient botApi,
        IConfiguration configuration,
        ILogger<BotUpdateHub> logger)
    {
        _scopeFactory = scopeFactory;
        _botApi = botApi;
        _logger = logger;

        // 检查是否启用 Webhook 模式
        _webhookEnabled = string.Equals(
            configuration["Telegram:WebhookEnabled"]?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Webhook 模式：注入 Telegram 推送的 update。
    /// 返回 true 表示成功处理，false 表示 token 无效或 bot 未启用。
    /// </summary>
    public async Task<bool> InjectWebhookUpdateAsync(string token, JsonElement update, CancellationToken cancellationToken)
    {
        token = (token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (_webhookReceivers.TryGetValue(token, out var fastReceiver))
        {
            fastReceiver.Inject(update);
            return true;
        }

        BotWebhookReceiver receiver;

        // 仅在“首次遇到某个 token”时加全局锁，避免高频 webhook 进入串行瓶颈。
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_webhookReceivers.TryGetValue(token, out receiver!))
            {
                // 验证 token 有效性并创建 receiver
                using var scope = _scopeFactory.CreateScope();
                var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
                var bot = await botRepo.GetByTokenAsync(token);
                if (bot == null || !bot.IsActive)
                    return false;

                receiver = new BotWebhookReceiver(bot.Id, token, _scopeFactory, _logger);
                _webhookReceivers[token] = receiver;
            }
        }
        finally
        {
            _gate.Release();
        }

        receiver.Inject(update);
        return true;
    }

    /// <summary>
    /// 将 Webhook 路径中的 token（推荐为 SHA256(token)）解析为真实的 bot token。
    /// </summary>
    public async Task<string?> ResolveBotTokenFromWebhookPathAsync(string pathToken, CancellationToken cancellationToken)
    {
        pathToken = (pathToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(pathToken))
            return null;

        // 兼容：若仍使用明文 bot token 作为路径（不推荐），直接返回。
        if (WebhookTokenHelper.IsLikelyPlainBotToken(pathToken))
            return pathToken;

        if (!WebhookTokenHelper.IsSha256Hex(pathToken))
            return null;

        var builtAt = _webhookTokenCacheBuiltAtUtc;
        if (builtAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - builtAt < WebhookTokenCacheTtl
            && _botTokenByWebhookPathToken.TryGetValue(pathToken, out var cached))
        {
            return cached;
        }

        await _webhookTokenCacheGate.WaitAsync(cancellationToken);
        try
        {
            builtAt = _webhookTokenCacheBuiltAtUtc;
            if (builtAt == DateTimeOffset.MinValue || DateTimeOffset.UtcNow - builtAt >= WebhookTokenCacheTtl)
                await RebuildWebhookTokenCacheAsync(cancellationToken);

            return _botTokenByWebhookPathToken.TryGetValue(pathToken, out var token) ? token : null;
        }
        finally
        {
            _webhookTokenCacheGate.Release();
        }
    }

    private async Task RebuildWebhookTokenCacheAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
        var bots = await botRepo.GetAllAsync();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bot in bots)
        {
            if (!bot.IsActive || string.IsNullOrWhiteSpace(bot.Token))
                continue;

            var token = bot.Token.Trim();
            if (string.IsNullOrWhiteSpace(token))
                continue;

            map[WebhookTokenHelper.ToWebhookPathToken(token)] = token;
        }

        _botTokenByWebhookPathToken = map;
        _webhookTokenCacheBuiltAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Webhook 模式下订阅更新（不启动轮询）。
    /// </summary>
    public async Task<BotUpdateSubscription> SubscribeWebhookAsync(int botId, CancellationToken cancellationToken)
    {
        if (botId <= 0)
            throw new ArgumentException("botId 无效", nameof(botId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
            var bot = await botRepo.GetByIdAsync(botId);
            if (bot == null)
                throw new InvalidOperationException($"Bot 不存在：{botId}");
            if (!bot.IsActive)
                throw new InvalidOperationException("Bot 未启用");

            var token = (bot.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Bot Token 为空");

            if (!_webhookReceivers.TryGetValue(token, out var receiver))
            {
                receiver = new BotWebhookReceiver(botId, token, _scopeFactory, _logger);
                _webhookReceivers[token] = receiver;
            }

            return receiver.Subscribe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BotUpdateSubscription> SubscribeAsync(int botId, CancellationToken cancellationToken)
    {
        // Webhook 模式下自动使用 Webhook 订阅（不启动轮询）
        if (_webhookEnabled)
        {
            _logger.LogDebug("Webhook mode enabled, using webhook subscription for bot {BotId}", botId);
            return await SubscribeWebhookAsync(botId, cancellationToken);
        }

        if (botId <= 0)
            throw new ArgumentException("botId 无效", nameof(botId));

        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
            var bot = await botRepo.GetByIdAsync(botId);
            if (bot == null)
                throw new InvalidOperationException($"Bot 不存在：{botId}");
            if (!bot.IsActive)
                throw new InvalidOperationException("Bot 未启用");

            var token = (bot.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Bot Token 为空");

            if (!_pollersByToken.TryGetValue(token, out var poller))
            {
                poller = await BotPoller.CreateAsync(botId, token, bot.LastUpdateId, _scopeFactory, _botApi, _logger, cancellationToken);
                _pollersByToken[token] = poller;
            }

            return poller.Subscribe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<BotPoller> pollers;
        List<BotWebhookReceiver> receivers;

        await _gate.WaitAsync();
        try
        {
            pollers = _pollersByToken.Values.ToList();
            _pollersByToken.Clear();

            receivers = _webhookReceivers.Values.ToList();
            _webhookReceivers.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var p in pollers)
        {
            try { await p.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Dispose bot poller failed: {BotId}", p.BotId); }
        }

        foreach (var r in receivers)
        {
            try { r.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Dispose webhook receiver failed: {BotId}", r.BotId); }
        }
    }

    public sealed class BotUpdateSubscription : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose;

        internal BotUpdateSubscription(int botId, ChannelReader<JsonElement> reader, Func<ValueTask> dispose)
        {
            BotId = botId;
            Reader = reader;
            _dispose = dispose;
        }

        public int BotId { get; }
        public ChannelReader<JsonElement> Reader { get; }

        public ValueTask DisposeAsync() => _dispose();
    }

    private sealed class BotPoller : IAsyncDisposable
    {
        private static readonly BoundedChannelOptions SubscriberChannelOptions = new(512)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        };

        private const string AllowedMyChatMemberOnlyJson = "[\"my_chat_member\"]";

        private readonly int _persistBotId;
        private readonly string _token;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TelegramBotApiClient _botApi;
        private readonly ILogger _logger;

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;

        private readonly object _subscribersLock = new();
        private readonly Dictionary<Guid, Channel<JsonElement>> _subscribers = new();

        private const int PendingMyChatMemberMax = 2000;
        private readonly object _pendingLock = new();
        private readonly Queue<JsonElement> _pendingMyChatMember = new();

        private long _nextOffset;

        public int BotId => _persistBotId;

        private BotPoller(
            int persistBotId,
            string token,
            long nextOffset,
            IReadOnlyList<JsonElement>? initialPendingMyChatMember,
            IServiceScopeFactory scopeFactory,
            TelegramBotApiClient botApi,
            ILogger logger)
        {
            _persistBotId = persistBotId;
            _token = token;
            _nextOffset = nextOffset;
            _scopeFactory = scopeFactory;
            _botApi = botApi;
            _logger = logger;

            if (initialPendingMyChatMember is { Count: > 0 })
            {
                lock (_pendingLock)
                {
                    foreach (var u in initialPendingMyChatMember)
                    {
                        _pendingMyChatMember.Enqueue(u);
                        while (_pendingMyChatMember.Count > PendingMyChatMemberMax)
                            _pendingMyChatMember.Dequeue();
                    }
                }
            }

            _loopTask = Task.Run(() => RunAsync(_cts.Token));
        }

        public static async Task<BotPoller> CreateAsync(
            int botId,
            string token,
            long? lastUpdateId,
            IServiceScopeFactory scopeFactory,
            TelegramBotApiClient botApi,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // 初始化 offset：
            // - 如果数据库里已有 LastUpdateId，则从其后开始
            // - 否则快进到最新，避免冷启动回放历史消息造成刷屏
            long nextOffset;
            List<JsonElement>? pendingMyChatMember = null;
            if (lastUpdateId.HasValue)
            {
                nextOffset = lastUpdateId.Value + 1;

                // 恢复时：从上次的 offset 开始收集 my_chat_member（用于发现新加入的频道）。
                // 这些 updates 稍后会被主循环重新拉取（幂等），但手动同步时可以立即使用缓存。
                pendingMyChatMember = await QuickCollectMyChatMemberAsync(
                    token, nextOffset, botApi, logger, cancellationToken);
            }
            else
            {
                // 重要：冷启动快进会丢历史消息（避免刷屏），但不能丢 my_chat_member（否则 Bot 加入私密频道永远同步不到）。
                var r = await FastForwardOffsetAndCollectMyChatMemberAsync(botId, token, scopeFactory, botApi, logger, cancellationToken);
                nextOffset = r.NextOffset;
                pendingMyChatMember = r.PendingMyChatMember;
            }

            return new BotPoller(botId, token, nextOffset, pendingMyChatMember, scopeFactory, botApi, logger);
        }

        /// <summary>
        /// 快速收集从指定 offset 开始的 my_chat_member updates（不推进主循环 offset）。
        /// 用于 Panel 恢复/手动同步时发现新加入的频道。
        /// </summary>
        private static async Task<List<JsonElement>?> QuickCollectMyChatMemberAsync(
            string token,
            long startOffset,
            TelegramBotApiClient botApi,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var pending = new List<JsonElement>();
            long offset = startOffset;

            // 最多尝试 20 次（2000 条），避免无限循环
            for (var i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var updates = await botApi.CallAsync(token, "getUpdates", new Dictionary<string, string?>
                    {
                        ["offset"] = offset.ToString(),
                        ["timeout"] = "0",  // 快速返回，不长轮询
                        ["limit"] = "100",
                        ["allowed_updates"] = AllowedMyChatMemberOnlyJson
                    }, cancellationToken);

                    if (updates.ValueKind != JsonValueKind.Array)
                        break;

                    long? maxUpdateId = null;
                    foreach (var u in updates.EnumerateArray())
                    {
                        if (!TryGetUpdateId(u, out var id))
                            continue;

                        maxUpdateId = maxUpdateId.HasValue ? Math.Max(maxUpdateId.Value, id) : id;

                        if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("my_chat_member", out _))
                        {
                            pending.Add(u.Clone());
                            if (pending.Count > PendingMyChatMemberMax)
                                pending.RemoveAt(0);
                        }
                    }

                    if (!maxUpdateId.HasValue)
                        break;

                    offset = maxUpdateId.Value + 1;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "QuickCollectMyChatMember failed at offset {Offset}", offset);
                    break;
                }
            }

            if (pending.Count > 0)
                logger.LogInformation("QuickCollectMyChatMember collected {Count} my_chat_member updates", pending.Count);

            return pending.Count > 0 ? pending : null;
        }

        public BotUpdateSubscription Subscribe()
        {
            var id = Guid.NewGuid();
            var ch = Channel.CreateBounded<JsonElement>(SubscriberChannelOptions);

            List<JsonElement>? pending = null;
            lock (_subscribersLock)
            {
                _subscribers[id] = ch;
            }

            lock (_pendingLock)
            {
                if (_pendingMyChatMember.Count > 0)
                {
                    pending = _pendingMyChatMember.ToList();
                    _pendingMyChatMember.Clear();
                }
            }

            if (pending != null)
            {
                foreach (var u in pending)
                {
                    // 尽力写入：满了就丢，避免首次订阅阻塞
                    ch.Writer.TryWrite(u);
                }
            }

            return new BotUpdateSubscription(_persistBotId, ch.Reader, async () =>
            {
                Channel<JsonElement>? removed = null;
                lock (_subscribersLock)
                {
                    if (_subscribers.Remove(id, out var existing))
                        removed = existing;
                }

                if (removed != null)
                {
                    try { removed.Writer.TryComplete(); }
                    catch { /* ignore */ }
                }

                await ValueTask.CompletedTask;
            });
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // 给启动期一点喘息
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { return; }

            var conflictStreak = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Bot 被停用（或 Token 被替换）时暂停轮询：
                    // 否则即使 UI 停用了 Bot，后台仍可能因为历史订阅而持续 getUpdates，导致 409/限流。
                    if (!await IsBotPollingEnabledAsync(cancellationToken))
                    {
                        conflictStreak = 0;
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    // 没有订阅者时也继续拉取并确认 offset，避免积压导致后续刷屏；
                    // 同时还能保证 Bot 频道自动同步等后台能力可工作。

                    var updates = await _botApi.CallAsync(_token, "getUpdates", new Dictionary<string, string?>
                    {
                        ["offset"] = _nextOffset.ToString(),
                        ["timeout"] = "25",
                        ["limit"] = "100",
                        ["allowed_updates"] = AllowedUpdatesJson
                    }, cancellationToken);

                    if (updates.ValueKind != JsonValueKind.Array)
                        continue;

                    long? maxUpdateId = null;
                    foreach (var update in updates.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!TryGetUpdateId(update, out var updateId))
                            continue;

                        maxUpdateId = maxUpdateId.HasValue ? Math.Max(maxUpdateId.Value, updateId) : updateId;

                        Broadcast(update);
                    }

                    if (maxUpdateId.HasValue)
                    {
                        _nextOffset = maxUpdateId.Value + 1;
                        await SaveLastUpdateIdAsync(maxUpdateId.Value, cancellationToken);
                    }

                    conflictStreak = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("getUpdates (409)", StringComparison.OrdinalIgnoreCase))
                {
                    // 典型原因：
                    // - 同一个 bot token 还有别的实例在 long-poll
                    // - 或者本进程有 bug 造成并发（理论上 Hub 已避免）
                    conflictStreak++;
                    var backoffSeconds = Math.Min(60, 2 * conflictStreak);
                    _logger.LogWarning(ex, "Bot getUpdates 409 conflict: botId={BotId}（请确保该 Bot Token 仅有一个实例在轮询 getUpdates）", _persistBotId);
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Bot update poll loop failed: botId={BotId}", _persistBotId);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }

        private async Task<bool> IsBotPollingEnabledAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
                var bot = await botRepo.GetByIdAsync(_persistBotId);
                if (bot == null || !bot.IsActive)
                    return false;

                var token = (bot.Token ?? string.Empty).Trim();
                if (!string.Equals(token, _token, StringComparison.Ordinal))
                {
                    // Bot Token 被替换了：停止旧 token 的轮询（新 token 会创建新 poller）
                    _logger.LogWarning("Bot token changed, pausing old poller: botId={BotId}", _persistBotId);
                    return false;
                }

                return true;
            }
            catch
            {
                // 配置/数据库短暂异常时不应导致 poller 永久停掉
                return true;
            }
        }

        private void Broadcast(JsonElement update)
        {
            // 即使当前没有订阅者，也要缓存 my_chat_member 更新：
            // 手动同步通常是“点按钮才订阅”，否则 poller 会把更新吃掉导致新增频道永远同步不到。
            if (update.ValueKind == JsonValueKind.Object && update.TryGetProperty("my_chat_member", out _))
            {
                lock (_pendingLock)
                {
                    _pendingMyChatMember.Enqueue(update.Clone());
                    while (_pendingMyChatMember.Count > PendingMyChatMemberMax)
                        _pendingMyChatMember.Dequeue();
                }
            }

            List<Channel<JsonElement>> targets;
            lock (_subscribersLock)
            {
                if (_subscribers.Count == 0)
                    return;
                targets = _subscribers.Values.ToList();
            }

            foreach (var ch in targets)
            {
                try
                {
                    // Clone：JsonElement 的生命周期绑定 JsonDocument
                    ch.Writer.TryWrite(update.Clone());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Broadcast update failed: botId={BotId}", _persistBotId);
                }
            }
        }

        private static bool TryGetUpdateId(JsonElement update, out long updateId)
        {
            updateId = 0;
            if (!update.TryGetProperty("update_id", out var el))
                return false;
            if (el.ValueKind != JsonValueKind.Number)
                return false;
            return el.TryGetInt64(out updateId);
        }

        private async Task SaveLastUpdateIdAsync(long updateId, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
            var bot = await botRepo.GetByIdAsync(_persistBotId);
            if (bot == null)
                return;

            if (!bot.LastUpdateId.HasValue || updateId > bot.LastUpdateId.Value)
            {
                bot.LastUpdateId = updateId;
                await botRepo.UpdateAsync(bot);
            }
        }

        private sealed record FastForwardResult(long NextOffset, List<JsonElement>? PendingMyChatMember);

        private static async Task<FastForwardResult> FastForwardOffsetAndCollectMyChatMemberAsync(
            int botId,
            string token,
            IServiceScopeFactory scopeFactory,
            TelegramBotApiClient botApi,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var pending = new List<JsonElement>();

            // A) 先尽力把 my_chat_member 全部捞出来（用于“Bot 新加入频道”的识别），避免被快进丢弃。
            long myOffset = 0;
            for (var i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var updates = await botApi.CallAsync(token, "getUpdates", new Dictionary<string, string?>
                {
                    ["offset"] = myOffset.ToString(),
                    ["timeout"] = "0",
                    ["limit"] = "100",
                    ["allowed_updates"] = AllowedMyChatMemberOnlyJson
                }, cancellationToken);

                if (updates.ValueKind != JsonValueKind.Array)
                    break;

                long? maxUpdateId = null;
                foreach (var u in updates.EnumerateArray())
                {
                    if (!TryGetUpdateId(u, out var id))
                        continue;

                    maxUpdateId = maxUpdateId.HasValue ? Math.Max(maxUpdateId.Value, id) : id;
                    if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("my_chat_member", out _))
                    {
                        pending.Add(u.Clone());
                        if (pending.Count > PendingMyChatMemberMax)
                            pending.RemoveAt(0);
                    }
                }

                if (!maxUpdateId.HasValue)
                    break;

                myOffset = maxUpdateId.Value + 1;
            }

            // B) 再快进到最新（覆盖所有 update 类型），避免冷启动回放历史消息刷屏。
            long offset = 0;

            // 尝试最多 20 次（2000 条）以“清空积压”，避免首次启用模块直接刷历史。
            for (var i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var updates = await botApi.CallAsync(token, "getUpdates", new Dictionary<string, string?>
                {
                    ["offset"] = offset.ToString(),
                    ["timeout"] = "0",
                    ["limit"] = "100",
                    ["allowed_updates"] = AllowedUpdatesJson
                }, cancellationToken);

                if (updates.ValueKind != JsonValueKind.Array)
                    break;

                long? maxUpdateId = null;
                foreach (var u in updates.EnumerateArray())
                {
                    if (!TryGetUpdateId(u, out var id))
                        continue;
                    maxUpdateId = maxUpdateId.HasValue ? Math.Max(maxUpdateId.Value, id) : id;
                }

                if (!maxUpdateId.HasValue)
                    break;

                offset = maxUpdateId.Value + 1;
            }

            // 写入数据库（用于下次启动直接从最新开始）
            try
            {
                using var scope = scopeFactory.CreateScope();
                var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
                var bot = await botRepo.GetByIdAsync(botId);
                if (bot != null && offset > 0)
                {
                    var last = offset - 1;
                    if (!bot.LastUpdateId.HasValue || last > bot.LastUpdateId.Value)
                    {
                        bot.LastUpdateId = last;
                        await botRepo.UpdateAsync(bot);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FastForwardOffset persistence failed: botId={BotId}", botId);
            }

            return new FastForwardResult(offset, pending.Count > 0 ? pending : null);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _loopTask; } catch { /* ignore */ }

            lock (_subscribersLock)
            {
                foreach (var ch in _subscribers.Values)
                {
                    try { ch.Writer.TryComplete(); } catch { /* ignore */ }
                }
                _subscribers.Clear();
            }

            _cts.Dispose();
        }
    }

    /// <summary>
    /// Webhook 模式的更新接收器（不启动轮询，仅接收注入的 update 并广播）。
    /// </summary>
    private sealed class BotWebhookReceiver
    {
        private static readonly BoundedChannelOptions SubscriberChannelOptions = new(512)
        {
            // Webhook 端点可能并发调用 Inject，因此这里必须允许多写入者；
            // 否则 SingleWriter=true 会触发 Channel 的非线程安全路径，导致偶发异常/卡死。
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        };

        private const int PendingMyChatMemberMax = 2000;

        private readonly int _botId;
        private readonly string _token;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;

        private readonly object _subscribersLock = new();
        private readonly Dictionary<Guid, Channel<JsonElement>> _subscribers = new();

        private readonly object _pendingLock = new();
        private readonly Queue<JsonElement> _pendingMyChatMember = new();

        private long _latestUpdateId = -1;
        private long _lastPersistedUpdateId = -1;
        private int _persistLoopRunning = 0;

        public int BotId => _botId;

        public BotWebhookReceiver(int botId, string token, IServiceScopeFactory scopeFactory, ILogger logger)
        {
            _botId = botId;
            _token = token;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public void Inject(JsonElement update)
        {
            if (update.ValueKind != JsonValueKind.Object)
                return;

            // 缓存 my_chat_member（用于手动同步）
            if (update.TryGetProperty("my_chat_member", out _))
            {
                lock (_pendingLock)
                {
                    _pendingMyChatMember.Enqueue(update.Clone());
                    while (_pendingMyChatMember.Count > PendingMyChatMemberMax)
                        _pendingMyChatMember.Dequeue();
                }
            }

            // 保存 update_id 到数据库
            if (update.TryGetProperty("update_id", out var updateIdEl) && updateIdEl.TryGetInt64(out var updateId))
            {
                UpdateLatestUpdateId(updateId);
                EnsurePersistLoopRunning();
            }

            // 广播给订阅者
            List<Channel<JsonElement>> targets;
            lock (_subscribersLock)
            {
                if (_subscribers.Count == 0)
                    return;
                targets = _subscribers.Values.ToList();
            }

            foreach (var ch in targets)
            {
                try
                {
                    ch.Writer.TryWrite(update.Clone());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Webhook broadcast failed: botId={BotId}", _botId);
                }
            }
        }

        public BotUpdateSubscription Subscribe()
        {
            var id = Guid.NewGuid();
            var ch = Channel.CreateBounded<JsonElement>(SubscriberChannelOptions);

            lock (_subscribersLock)
            {
                _subscribers[id] = ch;
            }

            // 把缓存的 my_chat_member 写入新订阅者
            List<JsonElement>? pending = null;
            lock (_pendingLock)
            {
                if (_pendingMyChatMember.Count > 0)
                {
                    pending = _pendingMyChatMember.ToList();
                    _pendingMyChatMember.Clear();
                }
            }

            if (pending != null)
            {
                foreach (var u in pending)
                    ch.Writer.TryWrite(u);
            }

            return new BotUpdateSubscription(_botId, ch.Reader, async () =>
            {
                Channel<JsonElement>? removed = null;
                lock (_subscribersLock)
                {
                    if (_subscribers.Remove(id, out var existing))
                        removed = existing;
                }

                if (removed != null)
                {
                    try { removed.Writer.TryComplete(); }
                    catch { /* ignore */ }
                }

                await ValueTask.CompletedTask;
            });
        }

        public void Dispose()
        {
            lock (_subscribersLock)
            {
                foreach (var ch in _subscribers.Values)
                {
                    try { ch.Writer.TryComplete(); }
                    catch { /* ignore */ }
                }
                _subscribers.Clear();
            }
        }

        private void UpdateLatestUpdateId(long updateId)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _latestUpdateId);
                if (updateId <= current)
                    return;
                if (Interlocked.CompareExchange(ref _latestUpdateId, updateId, current) == current)
                    return;
            }
        }

        private void EnsurePersistLoopRunning()
        {
            if (Interlocked.CompareExchange(ref _persistLoopRunning, 1, 0) != 0)
                return;
            _ = PersistLatestUpdateIdLoopAsync();
        }

        private async Task PersistLatestUpdateIdLoopAsync()
        {
            try
            {
                while (true)
                {
                    // 合并短时间内的多条 update：避免每条 update 都触发一次写库（线程池/SQLite 锁竞争）
                    await Task.Delay(TimeSpan.FromSeconds(1));

                    var latest = Interlocked.Read(ref _latestUpdateId);
                    if (latest <= _lastPersistedUpdateId)
                        break;

                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var botRepo = scope.ServiceProvider.GetRequiredService<IBotRepository>();
                        var bot = await botRepo.GetByIdAsync(_botId);
                        if (bot != null && (!bot.LastUpdateId.HasValue || latest > bot.LastUpdateId.Value))
                        {
                            bot.LastUpdateId = latest;
                            await botRepo.UpdateAsync(bot);
                        }
                        _lastPersistedUpdateId = latest;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save webhook update_id: botId={BotId} update_id={UpdateId}", _botId, latest);
                    }

                    // 若这段时间又来了新 update，则继续下一轮（保持单个循环，不扩散任务数）
                    if (Interlocked.Read(ref _latestUpdateId) <= _lastPersistedUpdateId)
                        break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _persistLoopRunning, 0);

                // 处理竞态：若在“准备退出”期间又来了新 update，则再次启动循环
                if (Interlocked.Read(ref _latestUpdateId) > _lastPersistedUpdateId
                    && Interlocked.CompareExchange(ref _persistLoopRunning, 1, 0) == 0)
                {
                    _ = PersistLatestUpdateIdLoopAsync();
                }
            }
        }
    }
}
