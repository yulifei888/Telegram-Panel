using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 批量任务后台执行器：从数据库拉取 pending 任务并在后台静默运行。
/// </summary>
public sealed class BatchTaskBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BatchTaskBackgroundService> _logger;
    private readonly IConfiguration _configuration;

    public BatchTaskBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<BatchTaskBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("BatchTasks:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Batch task runner disabled (BatchTasks:Enabled=false)");
            return;
        }

        var seconds = _configuration.GetValue("BatchTasks:PollIntervalSeconds", 2);
        if (seconds < 1) seconds = 1;
        if (seconds > 30) seconds = 30;
        var interval = TimeSpan.FromSeconds(seconds);

        _logger.LogInformation("Batch task runner started, interval {IntervalSeconds} seconds", seconds);

        // 延迟一点，避免与启动时 DB 迁移抢资源
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryRunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch task runner loop failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TryRunOneAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();

        var pending = (await taskManagement.GetTasksByStatusAsync("pending"))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault();

        if (pending == null)
            return;

        await taskManagement.StartTaskAsync(pending.Id);
        _logger.LogInformation("Batch task started: {TaskId} {TaskType}", pending.Id, pending.TaskType);

        var completed = pending.Completed;
        var failed = pending.Failed;

        try
        {
            // 模块扩展任务：从 DI 中查找对应 TaskType 的执行器
            var handler = scope.ServiceProvider
                .GetServices<IModuleTaskHandler>()
                .FirstOrDefault(h => string.Equals(h.TaskType, pending.TaskType, StringComparison.OrdinalIgnoreCase));

            if (handler == null)
            {
                failed = pending.Total == 0 ? 1 : pending.Total;
                await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed);
                await taskManagement.CompleteTaskAsync(pending.Id, success: false);
                _logger.LogWarning("Unsupported batch task type: {TaskType} (task {TaskId})", pending.TaskType, pending.Id);
                return;
            }

            var host = new DbBackedModuleTaskExecutionHost(pending, taskManagement, scope.ServiceProvider);
            await handler.ExecuteAsync(host, cancellationToken);

            var after = await taskManagement.GetTaskAsync(pending.Id);
            if (after != null)
            {
                completed = after.Completed;
                failed = after.Failed;
            }

            // 如果任务被用户取消（当前实现：Cancel 会把状态写成 failed），则不覆盖它
            var latest = await taskManagement.GetTaskAsync(pending.Id);
            if (latest != null && latest.Status != "running")
                return;

            await taskManagement.CompleteTaskAsync(pending.Id, success: failed == 0);
            _logger.LogInformation("Batch task completed: {TaskId} {TaskType} (ok={Ok}, completed={Completed}, failed={Failed})",
                pending.Id, pending.TaskType, failed == 0, completed, failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch task failed: {TaskId} {TaskType}", pending.Id, pending.TaskType);
            try
            {
                await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed == 0 ? 1 : failed);
                await taskManagement.CompleteTaskAsync(pending.Id, success: false);
            }
            catch
            {
                // ignore secondary failures
            }
        }
    }

    private sealed class DbBackedModuleTaskExecutionHost : IModuleTaskExecutionHost
    {
        private readonly BatchTask _task;
        private readonly BatchTaskManagementService _taskManagement;

        public DbBackedModuleTaskExecutionHost(BatchTask task, BatchTaskManagementService taskManagement, IServiceProvider services)
        {
            _task = task;
            _taskManagement = taskManagement;
            Services = services;
        }

        public int TaskId => _task.Id;
        public string TaskType => _task.TaskType;
        public int Total => _task.Total;
        public string? Config => _task.Config;
        public IServiceProvider Services { get; }

        public async Task<bool> IsStillRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await _taskManagement.GetTaskAsync(_task.Id);
            return latest != null && latest.Status == "running";
        }

        public async Task UpdateProgressAsync(int completed, int failed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _taskManagement.UpdateTaskProgressAsync(_task.Id, completed, failed);
        }
    }
}
