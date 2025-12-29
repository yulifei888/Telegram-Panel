using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Bot API 更新分发 Hub：
/// - 同一个 Bot Token 只允许一个 getUpdates 长轮询，否则会 409 Conflict
/// - 这里为每个 botId 维护一个单一轮询器，并把同一份 updates 广播给多个消费者（模块/后台服务）
/// </summary>
public sealed class BotUpdateHub : IAsyncDisposable
{
    // 固定允许的更新类型：覆盖当前项目使用场景（转发/监听/入群事件）
    private const string AllowedUpdatesJson = "[\"message\",\"edited_message\",\"channel_post\",\"edited_channel_post\",\"my_chat_member\"]";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramBotApiClient _botApi;
    private readonly ILogger<BotUpdateHub> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BotPoller> _pollersByToken = new(StringComparer.Ordinal);

    public BotUpdateHub(
        IServiceScopeFactory scopeFactory,
        TelegramBotApiClient botApi,
        ILogger<BotUpdateHub> logger)
    {
        _scopeFactory = scopeFactory;
        _botApi = botApi;
        _logger = logger;
    }

    public async Task<BotUpdateSubscription> SubscribeAsync(int botId, CancellationToken cancellationToken)
    {
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

        await _gate.WaitAsync();
        try
        {
            pollers = _pollersByToken.Values.ToList();
            _pollersByToken.Clear();
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
}
