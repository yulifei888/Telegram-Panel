using System.Collections.Concurrent;
using TelegramPanel.Core.Models;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 聚合账号侧收到的 Telegram 消息更新，供后台任务按账号/聊天做短期等待。
/// </summary>
public sealed class TelegramAccountUpdateHub
{
    private static readonly TimeSpan MessageRetention = TimeSpan.FromMinutes(10);
    private const int MaxMessagesPerAccount = 200;

    private readonly ConcurrentDictionary<int, ConcurrentQueue<TelegramAccountMessageUpdate>> _messagesByAccount = new();

    public Task PublishAsync(TelegramAccountMessageUpdate update)
    {
        var queue = _messagesByAccount.GetOrAdd(update.AccountId, _ => new ConcurrentQueue<TelegramAccountMessageUpdate>());
        queue.Enqueue(update);
        TrimQueue(queue);
        return Task.CompletedTask;
    }

    public IReadOnlyList<TelegramAccountMessageUpdate> GetRecentMessages(int accountId, DateTimeOffset sinceUtc)
    {
        if (!_messagesByAccount.TryGetValue(accountId, out var queue))
            return Array.Empty<TelegramAccountMessageUpdate>();

        return queue
            .ToArray()
            .Where(x => x.ReceivedAtUtc >= sinceUtc)
            .OrderBy(x => x.ReceivedAtUtc)
            .ToList();
    }

    public async Task<TelegramAccountMessageUpdate?> WaitForAsync(
        int accountId,
        Func<TelegramAccountMessageUpdate, bool> predicate,
        DateTimeOffset sinceUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(1);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = GetRecentMessages(accountId, sinceUtc);
            for (var index = items.Count - 1; index >= 0; index--)
            {
                var item = items[index];
                var key = $"{item.Message.id}:{item.ReceivedAtUtc.UtcTicks}:{(item.IsEdited ? 1 : 0)}";
                if (!seen.Add(key))
                    continue;

                if (predicate(item))
                    return item;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < TimeSpan.FromMilliseconds(300)
                ? remaining
                : TimeSpan.FromMilliseconds(300);
            await Task.Delay(delay, cancellationToken);
        }

        return null;
    }

    private static void TrimQueue(ConcurrentQueue<TelegramAccountMessageUpdate> queue)
    {
        var cutoff = DateTimeOffset.UtcNow - MessageRetention;

        while (queue.TryPeek(out var item))
        {
            if (queue.Count <= MaxMessagesPerAccount && item.ReceivedAtUtc >= cutoff)
                break;

            queue.TryDequeue(out _);
        }
    }
}
