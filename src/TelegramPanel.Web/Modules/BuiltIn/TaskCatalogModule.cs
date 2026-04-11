using MudBlazor;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Modules;
using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Modules.BuiltIn;

public sealed class TaskCatalogModule : ITelegramPanelModule, IModuleTaskProvider
{
    public TaskCatalogModule(string version)
    {
        Manifest = new ModuleManifest
        {
            Id = "builtin.tasks",
            Name = "任务：内置批量任务",
            Version = version,
            Host = new HostCompatibility(),
            Entry = new ModuleEntryPoint { Assembly = "", Type = typeof(TaskCatalogModule).FullName ?? "" }
        };
    }

    public ModuleManifest Manifest { get; }

    public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
    {
        // 内置任务的执行由宿主 BatchTaskBackgroundService 负责；这里只提供“元数据”用于 UI 展示与创建。
        services.AddSingleton<IModuleTaskRerunBuilder, UserChatActiveTaskRerunBuilder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
    {
        // 无 endpoints
    }

    public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
    {
        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.UserChatActive,
            DisplayName = "账号持续活跃（群组/频道）",
            Description = "按账号分类持续向指定目标发送词典内容，支持间隔抖动、随机/队列循环。",
            Icon = Icons.Material.Filled.Chat,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.UserChatActiveTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.UserChatActiveTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 120
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.ChannelGroupPrivateCreate,
            DisplayName = "自动创建私密频道/群组",
            Description = "按账号分类批量创建私密频道或群组，支持标题变量、固定头像/图片字典头像、数量上限与延时抖动。",
            Icon = Icons.Material.Filled.AddCircle,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPrivateCreateTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPrivateCreateTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 130
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.ChannelGroupPublicize,
            DisplayName = "私密频道/群组公开化",
            Description = "从系统创建的私密频道/群组中按分类挑选候选对象，批量设置标题、描述、用户名与头像后公开。",
            Icon = Icons.Material.Filled.Public,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPublicizeTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPublicizeTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 140
        };

        yield return new ModuleTaskDefinition
        {
            Category = "system",
            TaskType = BatchTaskTypes.ExternalApiKick,
            DisplayName = "外部 API：踢人/封禁",
            Description = "由外部接口触发并记录到任务中心（一般无需手动创建）。",
            Icon = Icons.Material.Filled.Link,
            Order = 1000
        };
    }
}
