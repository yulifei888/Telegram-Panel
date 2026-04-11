using Microsoft.Extensions.Logging;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Modules;

public sealed class ModuleContributionRegistry
{
    public ModuleContributionRegistry(ModuleRegistry registry, ILogger<ModuleContributionRegistry> logger)
    {
        var tasks = new List<RegisteredTaskDefinition>();
        var apis = new List<RegisteredApiTypeDefinition>();
        var pages = new List<RegisteredPageDefinition>();
        var navs = new List<RegisteredNavItem>();
        var diagnostics = new List<string>();

        foreach (var m in registry.Modules)
        {
            if (m.Instance is IModuleTaskProvider taskProvider)
            {
                foreach (var t in taskProvider.GetTasks(m.Context) ?? Array.Empty<ModuleTaskDefinition>())
                {
                    tasks.Add(new RegisteredTaskDefinition(m, NormalizeTask(t)));
                }
            }

            if (m.Instance is IModuleApiProvider apiProvider)
            {
                foreach (var a in apiProvider.GetApis(m.Context) ?? Array.Empty<ModuleApiTypeDefinition>())
                {
                    apis.Add(new RegisteredApiTypeDefinition(m, NormalizeApi(a)));
                }
            }

            if (m.Instance is IModuleUiProvider uiProvider)
            {
                foreach (var p in uiProvider.GetPages(m.Context) ?? Array.Empty<ModulePageDefinition>())
                {
                    pages.Add(new RegisteredPageDefinition(m, NormalizePage(p)));
                }
                foreach (var n in uiProvider.GetNavItems(m.Context) ?? Array.Empty<ModuleNavItem>())
                {
                    navs.Add(new RegisteredNavItem(m, NormalizeNav(n)));
                }
            }
        }

        Tasks = tasks;
        ApiTypes = apis;
        Pages = pages;
        NavItems = navs;

        TaskTypeToDefinition = BuildTaskIndex(tasks, diagnostics, logger);
        ApiTypeToDefinition = BuildApiIndex(apis, diagnostics, logger);
        PageKeyToDefinition = BuildPageIndex(pages, diagnostics, logger);
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<RegisteredTaskDefinition> Tasks { get; }
    public IReadOnlyList<RegisteredApiTypeDefinition> ApiTypes { get; }
    public IReadOnlyList<RegisteredPageDefinition> Pages { get; }
    public IReadOnlyList<RegisteredNavItem> NavItems { get; }

    public IReadOnlyDictionary<string, RegisteredTaskDefinition> TaskTypeToDefinition { get; }
    public IReadOnlyDictionary<string, RegisteredApiTypeDefinition> ApiTypeToDefinition { get; }
    public IReadOnlyDictionary<(string moduleId, string pageKey), RegisteredPageDefinition> PageKeyToDefinition { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    private static IReadOnlyDictionary<string, RegisteredTaskDefinition> BuildTaskIndex(
        List<RegisteredTaskDefinition> list,
        List<string> diagnostics,
        ILogger logger)
    {
        var dict = new Dictionary<string, RegisteredTaskDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in list)
        {
            var key = (t.Definition.TaskType ?? "").Trim();
            if (key.Length == 0)
                continue;

            if (dict.ContainsKey(key))
            {
                var msg = $"任务类型冲突：{key} 同时来自 {dict[key].Module.Id} 与 {t.Module.Id}，已忽略后者";
                diagnostics.Add(msg);
                logger.LogWarning(msg);
                continue;
            }

            dict[key] = t;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, RegisteredApiTypeDefinition> BuildApiIndex(
        List<RegisteredApiTypeDefinition> list,
        List<string> diagnostics,
        ILogger logger)
    {
        var dict = new Dictionary<string, RegisteredApiTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list)
        {
            var key = (a.Definition.Type ?? "").Trim();
            if (key.Length == 0)
                continue;
            if (dict.ContainsKey(key))
            {
                var msg = $"API 类型冲突：{key} 同时来自 {dict[key].Module.Id} 与 {a.Module.Id}，已忽略后者";
                diagnostics.Add(msg);
                logger.LogWarning(msg);
                continue;
            }
            dict[key] = a;
        }
        return dict;
    }

    private static IReadOnlyDictionary<(string moduleId, string pageKey), RegisteredPageDefinition> BuildPageIndex(
        List<RegisteredPageDefinition> list,
        List<string> diagnostics,
        ILogger logger)
    {
        var dict = new Dictionary<(string moduleId, string pageKey), RegisteredPageDefinition>();
        foreach (var p in list)
        {
            var moduleId = p.Module.Id;
            var key = (p.Definition.Key ?? "").Trim();
            if (key.Length == 0)
                continue;
            var k = (moduleId, key);
            if (dict.ContainsKey(k))
            {
                var msg = $"模块页面 Key 冲突：{moduleId}/{key} 重复定义，已保留第一个";
                diagnostics.Add(msg);
                logger.LogWarning(msg);
                continue;
            }
            dict[k] = p;
        }
        return dict;
    }

    private static ModuleTaskDefinition NormalizeTask(ModuleTaskDefinition t)
    {
        t.Category = (t.Category ?? "").Trim();
        t.TaskType = (t.TaskType ?? "").Trim();
        t.DisplayName = (t.DisplayName ?? "").Trim();
        t.Description = (t.Description ?? "").Trim();
        t.Icon = (t.Icon ?? "").Trim();
        t.CreateRoute = (t.CreateRoute ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t.CreateRoute)) t.CreateRoute = null;
        t.EditorComponentType = (t.EditorComponentType ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t.EditorComponentType)) t.EditorComponentType = null;
        t.TaskCenter ??= new ModuleTaskCenterCapabilities();
        t.TaskCenter.EditComponentType = (t.TaskCenter.EditComponentType ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t.TaskCenter.EditComponentType)) t.TaskCenter.EditComponentType = null;
        return t;
    }

    private static ModuleApiTypeDefinition NormalizeApi(ModuleApiTypeDefinition a)
    {
        a.Type = (a.Type ?? "").Trim();
        a.DisplayName = (a.DisplayName ?? "").Trim();
        a.Route = (a.Route ?? "").Trim();
        a.Description = (a.Description ?? "").Trim();
        return a;
    }

    private static ModulePageDefinition NormalizePage(ModulePageDefinition p)
    {
        p.Key = (p.Key ?? "").Trim();
        p.Title = (p.Title ?? "").Trim();
        p.Icon = (p.Icon ?? "").Trim();
        p.ComponentType = (p.ComponentType ?? "").Trim();
        p.Group = (p.Group ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p.Group)) p.Group = null;
        return p;
    }

    private static ModuleNavItem NormalizeNav(ModuleNavItem n)
    {
        n.Title = (n.Title ?? "").Trim();
        n.Href = (n.Href ?? "").Trim();
        n.Icon = (n.Icon ?? "").Trim();
        n.Group = (n.Group ?? "").Trim();
        if (string.IsNullOrWhiteSpace(n.Group)) n.Group = null;
        return n;
    }
}

public sealed record RegisteredTaskDefinition(LoadedModule Module, ModuleTaskDefinition Definition);
public sealed record RegisteredApiTypeDefinition(LoadedModule Module, ModuleApiTypeDefinition Definition);
public sealed record RegisteredPageDefinition(LoadedModule Module, ModulePageDefinition Definition);
public sealed record RegisteredNavItem(LoadedModule Module, ModuleNavItem Definition);
