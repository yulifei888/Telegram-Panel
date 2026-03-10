using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 计划任务业务服务。
/// </summary>
public sealed class ScheduledTaskService
{
    private readonly IScheduledTaskRepository _scheduledTaskRepository;
    private readonly CronExpressionService _cronExpressionService;
    private readonly ImageAssetStorageService _assetStorage;

    public ScheduledTaskService(
        IScheduledTaskRepository scheduledTaskRepository,
        CronExpressionService cronExpressionService,
        ImageAssetStorageService assetStorage)
    {
        _scheduledTaskRepository = scheduledTaskRepository;
        _cronExpressionService = cronExpressionService;
        _assetStorage = assetStorage;
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetAllOrderedAsync(cancellationToken);
    }

    public async Task<ScheduledTask?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetByIdAsync(id);
    }

    public async Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        Normalize(task);
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = _cronExpressionService.GetNextOccurrenceUtc(task.CronExpression, DateTime.UtcNow);
        return await _scheduledTaskRepository.AddAsync(task);
    }

    public async Task<ScheduledTask> UpdateAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        Normalize(task);
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = _cronExpressionService.GetNextOccurrenceUtc(task.CronExpression, DateTime.UtcNow);
        await _scheduledTaskRepository.UpdateAsync(task);
        return task;
    }

    public async Task PauseAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.Status = ScheduledTaskStatuses.Paused;
        task.UpdatedAt = DateTime.UtcNow;
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task ResumeAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.Status = ScheduledTaskStatuses.Enabled;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = _cronExpressionService.GetNextOccurrenceUtc(task.CronExpression, DateTime.UtcNow);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        if (!string.IsNullOrWhiteSpace(task.OwnedAssetScopeId))
            await _assetStorage.DeleteScopeAsync(task.OwnedAssetScopeId, cancellationToken);

        await _scheduledTaskRepository.DeleteAsync(task);
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetEnabledAsync(cancellationToken);
    }

    public async Task MarkTriggeredAsync(int id, DateTime triggeredAtUtc, int batchTaskId, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.LastRunAtUtc = triggeredAtUtc;
        task.LastBatchTaskId = batchTaskId;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = _cronExpressionService.GetNextOccurrenceUtc(task.CronExpression, triggeredAtUtc);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task AdvanceNextRunAsync(int id, DateTime fromUtc, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = _cronExpressionService.GetNextOccurrenceUtc(task.CronExpression, fromUtc);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public string ValidateCronOrThrow(string expression)
    {
        if (!_cronExpressionService.TryValidate(expression, out var error))
            throw new InvalidOperationException(error ?? "Cron 表达式无效");
        return expression.Trim();
    }

    private void Normalize(ScheduledTask task)
    {
        task.TaskType = (task.TaskType ?? string.Empty).Trim();
        task.Status = string.Equals((task.Status ?? string.Empty).Trim(), ScheduledTaskStatuses.Paused, StringComparison.OrdinalIgnoreCase)
            ? ScheduledTaskStatuses.Paused
            : ScheduledTaskStatuses.Enabled;
        task.CronExpression = ValidateCronOrThrow(task.CronExpression);
        task.ConfigJson = string.IsNullOrWhiteSpace(task.ConfigJson) ? null : task.ConfigJson.Trim();
        task.OwnedAssetScopeId = NormalizeNullable(task.OwnedAssetScopeId);
        if (task.Total < 0)
            task.Total = 0;
    }

    private static string? NormalizeNullable(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
