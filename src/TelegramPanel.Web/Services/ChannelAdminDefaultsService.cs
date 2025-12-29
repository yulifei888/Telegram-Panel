using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using TelegramPanel.Core.Interfaces;

namespace TelegramPanel.Web.Services;

public sealed record ChannelAdminDefaults(AdminRights Rights);

/// <summary>
/// 用户账号“设置管理员”默认权限（保存到 appsettings.local.json）
/// </summary>
public sealed class ChannelAdminDefaultsService
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ChannelAdminDefaultsService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configFilePath = LocalConfigFile.ResolvePath(configuration, environment);
    }

    public async Task<ChannelAdminDefaults?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return null;

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
                return null;

            if (root["ChannelAdminDefaults"] is not JsonObject section)
                return null;

            var rightsNode = section["Rights"];
            if (rightsNode is not JsonValue rv)
                return null;

            // 允许 int 或 string
            int mask;
            if (rv.TryGetValue<int>(out var i))
                mask = i;
            else if (rv.TryGetValue<string>(out var s) && int.TryParse(s, out var si))
                mask = si;
            else
                return null;

            var rights = (AdminRights)mask;
            return new ChannelAdminDefaults(rights);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(ChannelAdminDefaults defaults, CancellationToken cancellationToken = default)
    {
        if (defaults == null)
            throw new ArgumentNullException(nameof(defaults));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await LocalConfigFile.EnsureExistsAsync(_configFilePath, cancellationToken);

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            var section = root["ChannelAdminDefaults"] as JsonObject ?? new JsonObject();
            section["Rights"] = (int)defaults.Rights;
            root["ChannelAdminDefaults"] = section;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var updatedJson = JsonSerializer.Serialize(root, options);
            await LocalConfigFile.WriteJsonAtomicallyAsync(_configFilePath, updatedJson, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

