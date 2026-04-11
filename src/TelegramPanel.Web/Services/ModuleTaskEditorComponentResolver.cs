using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;

namespace TelegramPanel.Web.Services;

public static class ModuleTaskEditorComponentResolver
{
    public static Type? ResolveCreateEditor(RegisteredTaskDefinition taskDefinition)
    {
        return Resolve(taskDefinition, taskDefinition.Definition.EditorComponentType);
    }

    public static Type? ResolveEditEditor(RegisteredTaskDefinition taskDefinition)
    {
        var typeName = taskDefinition.Definition.TaskCenter.EditComponentType;
        if (string.IsNullOrWhiteSpace(typeName))
            typeName = taskDefinition.Definition.EditorComponentType;

        return Resolve(taskDefinition, typeName);
    }

    private static Type? Resolve(RegisteredTaskDefinition taskDefinition, string? typeName)
    {
        typeName = (typeName ?? string.Empty).Trim();
        if (typeName.Length == 0)
            return null;

        var moduleTypeName = NormalizeTypeName(typeName);
        return Type.GetType(typeName, throwOnError: false, ignoreCase: false)
               ?? taskDefinition.Module.Instance.GetType().Assembly.GetType(moduleTypeName, throwOnError: false, ignoreCase: false);
    }

    public static string NormalizeTypeName(string? typeName)
    {
        typeName = (typeName ?? string.Empty).Trim();
        if (typeName.Length == 0)
            return typeName;

        var comma = typeName.IndexOf(',', StringComparison.Ordinal);
        return comma > 0 ? typeName[..comma].Trim() : typeName;
    }
}
