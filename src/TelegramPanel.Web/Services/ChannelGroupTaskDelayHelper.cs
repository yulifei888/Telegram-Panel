using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 频道/群组任务延时辅助工具。
/// </summary>
public static class ChannelGroupTaskDelayHelper
{
    public static int NormalizeMinDelay(int value) => Math.Max(0, value);

    public static int NormalizeMaxDelay(int minValue, int maxValue)
    {
        minValue = NormalizeMinDelay(minValue);
        maxValue = Math.Max(0, maxValue);
        return Math.Max(minValue, maxValue);
    }

    public static int NormalizeJitterPercent(int value) => Math.Clamp(value, 0, 100);

    public static int ComputeDelayMilliseconds(int minSeconds, int maxSeconds, int jitterPercent)
    {
        minSeconds = NormalizeMinDelay(minSeconds);
        maxSeconds = NormalizeMaxDelay(minSeconds, maxSeconds);
        jitterPercent = NormalizeJitterPercent(jitterPercent);

        var baseSeconds = maxSeconds <= minSeconds
            ? minSeconds
            : Random.Shared.Next(minSeconds, maxSeconds + 1);

        var factor = 1d;
        if (jitterPercent > 0)
        {
            var minFactor = 1d - jitterPercent / 100d;
            var maxFactor = 1d + jitterPercent / 100d;
            factor = minFactor + Random.Shared.NextDouble() * (maxFactor - minFactor);
        }

        var seconds = Math.Max(0, (int)Math.Ceiling(baseSeconds * factor));
        return seconds * 1000;
    }

    public static async Task<bool> DelayAsync(
        IModuleTaskExecutionHost host,
        int minSeconds,
        int maxSeconds,
        int jitterPercent,
        CancellationToken cancellationToken)
    {
        var delayMs = ComputeDelayMilliseconds(minSeconds, maxSeconds, jitterPercent);
        if (delayMs <= 0)
            return true;

        var remaining = delayMs;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
                return false;

            var chunk = Math.Min(remaining, 1000);
            await Task.Delay(chunk, cancellationToken);
            remaining -= chunk;
        }

        return true;
    }
}
