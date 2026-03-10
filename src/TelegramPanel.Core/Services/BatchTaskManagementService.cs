using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 批量任务管理服务
/// </summary>
public class BatchTaskManagementService
{
    private readonly IBatchTaskRepository _batchTaskRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BatchTaskManagementService> _logger;

    public BatchTaskManagementService(
        IBatchTaskRepository batchTaskRepository,
        IConfiguration configuration,
        ILogger<BatchTaskManagementService> logger)
    {
        _batchTaskRepository = batchTaskRepository;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<BatchTask?> GetTaskAsync(int id)
    {
        return await _batchTaskRepository.GetFreshByIdAsync(id);
    }

    public async Task<IEnumerable<BatchTask>> GetAllTasksAsync()
    {
        return await _batchTaskRepository.GetAllAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetTasksByStatusAsync(string status)
    {
        return await _batchTaskRepository.GetByStatusAsync(status);
    }

    public async Task<IEnumerable<BatchTask>> GetRunningTasksAsync()
    {
        return await _batchTaskRepository.GetRunningTasksAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20)
    {
        return await _batchTaskRepository.GetRecentTasksAsync(count);
    }

    public async Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.TrimHistoryTasksAsync(keepCount, cancellationToken);
    }

    public async Task<BatchTask> CreateTaskAsync(BatchTask task)
    {
        task.CreatedAt = DateTime.UtcNow;
        task.Status = "pending";
        return await _batchTaskRepository.AddAsync(task);
    }

    public async Task UpdateTaskProgressAsync(int taskId, int completed, int failed)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task != null)
        {
            task.Completed = completed;
            task.Failed = failed;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task UpdateTaskConfigAsync(int taskId, string? config)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task != null)
        {
            task.Config = config;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task UpdateTaskDraftAsync(int taskId, int total, string? config)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task != null)
        {
            if (total < 0) total = 0;
            task.Total = total;
            task.Config = config;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task StartTaskAsync(int taskId)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task != null)
        {
            task.Status = "running";
            task.StartedAt = DateTime.UtcNow;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task PauseTaskAsync(int taskId)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task == null)
            return;

        if (task.Status is "running" or "pending")
        {
            task.Status = "paused";
            task.CompletedAt = null;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task ResumeTaskAsync(int taskId)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task == null)
            return;

        if (task.Status == "paused")
        {
            task.Status = "pending";
            task.StartedAt = null;
            task.CompletedAt = null;
            await _batchTaskRepository.UpdateFreshAsync(task);
        }
    }

    public async Task CompleteTaskAsync(int taskId, bool success = true)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(taskId);
        if (task == null)
            return;

        task.Status = success ? "completed" : "failed";
        task.CompletedAt = DateTime.UtcNow;
        await _batchTaskRepository.UpdateFreshAsync(task);
        await TrimHistoryTasksIfNeededAsync();
    }

    public async Task DeleteTaskAsync(int id)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(id);
        if (task != null)
        {
            await _batchTaskRepository.DeleteAsync(task);
        }
    }

    private async Task TrimHistoryTasksIfNeededAsync(CancellationToken cancellationToken = default)
    {
        var keepCount = GetHistoryRetentionLimit();
        if (keepCount <= 0)
            return;

        var deletedCount = await _batchTaskRepository.TrimHistoryTasksAsync(keepCount, cancellationToken);
        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Trimmed {DeletedCount} historical batch tasks, keepCount={KeepCount}",
                deletedCount,
                keepCount);
        }
    }

    private int GetHistoryRetentionLimit()
    {
        var rawValue = _configuration["BatchTasks:HistoryRetentionLimit"];
        if (!int.TryParse(rawValue, out var keepCount) || keepCount < 0)
            return 0;

        return Math.Min(keepCount, 5000);
    }
}
