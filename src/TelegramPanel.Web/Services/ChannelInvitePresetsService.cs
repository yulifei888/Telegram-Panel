using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace TelegramPanel.Web.Services;

public sealed record ChannelInvitePreset(string Name, IReadOnlyList<string> Usernames);

/// <summary>
/// 批量邀请用户名预设（保存到 appsettings.local.json）
/// </summary>
public sealed class ChannelInvitePresetsService
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ChannelInvitePresetsService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configFilePath = LocalConfigFile.ResolvePath(configuration, environment);
    }

    public async Task<IReadOnlyList<ChannelInvitePreset>> GetPresetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return Array.Empty<ChannelInvitePreset>();

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
                return Array.Empty<ChannelInvitePreset>();

            if (root["ChannelInvitePresets"] is not JsonObject section)
                return Array.Empty<ChannelInvitePreset>();

            if (section["Presets"] is not JsonObject presetsObj)
                return Array.Empty<ChannelInvitePreset>();

            var list = new List<ChannelInvitePreset>();
            foreach (var kv in presetsObj)
            {
                var name = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var usernames = new List<string>();
                if (kv.Value is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        if (node is not JsonValue v)
                            continue;
                        if (!v.TryGetValue<string>(out var s))
                            continue;

                        var u = (s ?? "").Trim();
                        if (u.Length == 0)
                            continue;

                        u = u.TrimStart('@');
                        if (u.Length == 0)
                            continue;

                        usernames.Add(u);
                    }
                }

                usernames = usernames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (usernames.Count == 0)
                    continue;

                list.Add(new ChannelInvitePreset(name, usernames));
            }

            return list
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<ChannelInvitePreset>();
        }
    }

    public async Task SavePresetAsync(string name, IReadOnlyList<string> usernames, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("预设名称不能为空", nameof(name));

        var list = (usernames ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim().TrimStart('@'))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            throw new ArgumentException("预设用户名不能为空", nameof(usernames));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await LocalConfigFile.EnsureExistsAsync(_configFilePath, cancellationToken);

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            var section = root["ChannelInvitePresets"] as JsonObject ?? new JsonObject();
            var presetsObj = section["Presets"] as JsonObject ?? new JsonObject();

            var arr = new JsonArray();
            foreach (var u in list)
                arr.Add(u);

            presetsObj[name] = arr;
            section["Presets"] = presetsObj;
            root["ChannelInvitePresets"] = section;

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

    public async Task DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_configFilePath))
                return;

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
                return;

            if (root["ChannelInvitePresets"] is not JsonObject section)
                return;

            if (section["Presets"] is not JsonObject presetsObj)
                return;

            if (!presetsObj.Remove(name))
                return;

            section["Presets"] = presetsObj;
            root["ChannelInvitePresets"] = section;

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

