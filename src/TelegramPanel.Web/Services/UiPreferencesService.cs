using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace TelegramPanel.Web.Services;

/// <summary>
/// UI 偏好设置服务，用于管理用户界面相关的配置（如主题模式）
/// </summary>
public class UiPreferencesService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public UiPreferencesService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
        _configFilePath = LocalConfigFile.ResolvePath(configuration, environment);
    }

    /// <summary>
    /// 获取当前的深色模式设置
    /// </summary>
    public async Task<bool> GetIsDarkModeAsync()
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return true; // 默认使用深色模式

            var json = await File.ReadAllTextAsync(_configFilePath);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("UI", out var uiSection) &&
                uiSection.TryGetProperty("IsDarkMode", out var isDarkModeValue))
            {
                return isDarkModeValue.GetBoolean();
            }

            return true; // 默认使用深色模式
        }
        catch
        {
            return true; // 出错时默认使用深色模式
        }
    }

    /// <summary>
    /// 设置深色模式
    /// </summary>
    public async Task SetIsDarkModeAsync(bool isDarkMode)
    {
        await _writeLock.WaitAsync();
        try
        {
            // 确保配置文件存在
            await LocalConfigFile.EnsureExistsAsync(_configFilePath);

            // 读取现有配置
            var json = await File.ReadAllTextAsync(_configFilePath);
            var jsonObj = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            // 更新 UI 配置
            if (!jsonObj.ContainsKey("UI"))
            {
                jsonObj["UI"] = new JsonObject();
            }

            var uiSection = jsonObj["UI"]!.AsObject();
            uiSection["IsDarkMode"] = isDarkMode;

            // 写入配置文件
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var updatedJson = JsonSerializer.Serialize(jsonObj, options);
            await LocalConfigFile.WriteJsonAtomicallyAsync(_configFilePath, updatedJson);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
