using System.Text.Json.Nodes;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 任务配置中的图片资产作用域辅助工具。
/// </summary>
public static class TaskAssetScopeHelper
{
    private const string AssetScopeIdProperty = "asset_scope_id";

    public static string? GetAssetScopeId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;

        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            var scopeId = node?[AssetScopeIdProperty]?.GetValue<string>();
            return Normalize(scopeId);
        }
        catch
        {
            return null;
        }
    }

    public static string? RemoveAssetScopeId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return configJson;

        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
                return configJson;

            node.Remove(AssetScopeIdProperty);
            return LocalConfigFile.ToIndentedJson(node);
        }
        catch
        {
            return configJson;
        }
    }

    public static string? SetAssetScopeId(string? configJson, string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return configJson;

        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
                return configJson;

            var normalized = Normalize(scopeId);
            if (normalized == null)
                node.Remove(AssetScopeIdProperty);
            else
                node[AssetScopeIdProperty] = normalized;

            return LocalConfigFile.ToIndentedJson(node);
        }
        catch
        {
            return configJson;
        }
    }

    private static string? Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
