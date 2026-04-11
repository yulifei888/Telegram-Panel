using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Web.Services;

public enum ImageAssetKind
{
    General,
    Avatar
}

public sealed record StoredImageAssetInfo(string AssetPath, string FileName)
{
    public string PublicUrl => "/" + AssetPath.Replace('\\', '/');
}

/// <summary>
/// 图片资产存储服务。
/// </summary>
public sealed class ImageAssetStorageService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ImageAssetStorageService> _logger;

    public ImageAssetStorageService(IWebHostEnvironment environment, ILogger<ImageAssetStorageService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<StoredImageAssetInfo> SaveAsync(
        Stream fileStream,
        string fileName,
        string scope,
        ImageAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        scope = NormalizeScope(scope);
        if (scope.Length == 0)
            throw new ArgumentException("图片作用域不能为空", nameof(scope));

        fileName = (fileName ?? "image.jpg").Trim();
        if (fileName.Length == 0)
            fileName = "image.jpg";

        var extension = ".jpg";
        var relativeDir = Path.Combine("uploads", scope).Replace('\\', '/');
        var root = GetStorageRootPath();
        var fullDir = Path.Combine(root, "uploads", scope.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fullDir);

        var storedName = $"{Guid.NewGuid():N}{extension}";
        var relativePath = Path.Combine(relativeDir, storedName).Replace('\\', '/');
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        await using var normalized = kind == ImageAssetKind.Avatar
            ? await TelegramImageProcessor.PrepareAvatarJpegAsync(fileStream, cancellationToken)
            : await TelegramImageProcessor.PrepareStoredImageJpegAsync(fileStream, cancellationToken: cancellationToken);

        await using var output = File.Create(fullPath);
        normalized.Position = 0;
        await normalized.CopyToAsync(output, cancellationToken);

        return new StoredImageAssetInfo(relativePath, fileName);
    }

    public async Task<StoredImageAssetInfo> SaveBrowserFileAsync(
        IBrowserFile file,
        string scope,
        ImageAssetKind kind,
        long maxBytes = 20 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream(maxBytes, cancellationToken);
        return await SaveAsync(stream, file.Name, scope, kind, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        assetPath = NormalizeAssetPath(assetPath);
        var fullPath = Path.Combine(GetStorageRootPath(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("图片资产不存在", fullPath);

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAssetAsync(string? assetPath, CancellationToken cancellationToken = default)
    {
        assetPath = NormalizeAssetPath(assetPath);
        if (assetPath.Length == 0)
            return Task.CompletedTask;

        var fullPath = Path.Combine(GetStorageRootPath(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除图片资产失败：{AssetPath}", assetPath);
        }

        return Task.CompletedTask;
    }

    public Task DeleteScopeAsync(string? scope, CancellationToken cancellationToken = default)
    {
        scope = NormalizeScope(scope);
        if (scope.Length == 0)
            return Task.CompletedTask;

        var fullDir = Path.Combine(GetStorageRootPath(), "uploads", scope.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (Directory.Exists(fullDir))
                Directory.Delete(fullDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除图片作用域失败：{Scope}", scope);
        }

        return Task.CompletedTask;
    }

    private string GetStorageRootPath()
    {
        if (Directory.Exists("/data"))
        {
            Directory.CreateDirectory("/data/uploads");
            return "/data";
        }

        var path = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        Directory.CreateDirectory(path);
        return path;
    }

    private static string NormalizeScope(string? scope)
    {
        var raw = (scope ?? string.Empty).Trim().Replace('\\', '/');
        if (raw.Length == 0)
            return string.Empty;

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSegment)
            .Where(x => x.Length > 0)
            .ToArray();

        return string.Join('/', parts);
    }

    private static string NormalizeAssetPath(string? assetPath)
    {
        var raw = (assetPath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (raw.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("非法图片路径");

        return raw;
    }

    private static string SanitizeSegment(string value)
    {
        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            .ToArray();
        return new string(chars);
    }
}
