using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 账号导入协调服务 - 整合Session导入和数据库保存
/// </summary>
public class AccountImportService
{
    private readonly ISessionImporter _sessionImporter;
    private readonly AccountManagementService _accountManagement;
    private readonly ILogger<AccountImportService> _logger;

    public AccountImportService(
        ISessionImporter sessionImporter,
        AccountManagementService accountManagement,
        ILogger<AccountImportService> logger)
    {
        _sessionImporter = sessionImporter;
        _accountManagement = accountManagement;
        _logger = logger;
    }

    /// <summary>
    /// 从浏览器上传的文件导入账号
    /// </summary>
    public async Task<List<ImportResult>> ImportFromBrowserFilesAsync(
        IReadOnlyList<IBrowserFile> files,
        int apiId,
        string apiHash,
        int? categoryId = null)
    {
        var results = new List<ImportResult>();

        foreach (var file in files)
        {
            try
            {
                // 保存文件到临时目录
                var tempPath = Path.Combine(Path.GetTempPath(), file.Name);
                await using var fileStream = new FileStream(tempPath, FileMode.Create);
                await file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024) // 10MB limit
                    .CopyToAsync(fileStream);

                fileStream.Close();

                // 导入Session
                var result = await _sessionImporter.ImportFromSessionFileAsync(tempPath, apiId, apiHash);

                // 如果导入成功，保存到数据库
                if (result.Success && result.UserId.HasValue)
                {
                    var account = new Account
                    {
                        Phone = result.Phone!,
                        UserId = result.UserId.Value,
                        Username = result.Username,
                        SessionPath = result.SessionPath!,
                        ApiId = apiId,
                        ApiHash = apiHash,
                        IsActive = true,
                        CategoryId = categoryId,
                        CreatedAt = DateTime.UtcNow,
                        LastSyncAt = DateTime.UtcNow
                    };

                    try
                    {
                        await _accountManagement.CreateAccountAsync(account);
                        _logger.LogInformation("Account saved to database: {Phone}", account.Phone);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save account to database: {Phone}", account.Phone);
                        result = new ImportResult(
                            false,
                            result.Phone,
                            result.UserId,
                            result.Username,
                            result.SessionPath,
                            $"Session imported but database save failed: {ex.Message}"
                        );
                    }
                }

                results.Add(result);

                // 清理临时文件
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 忽略临时文件删除失败
                }

                // 延迟避免频繁连接
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process file: {FileName}", file.Name);
                results.Add(new ImportResult(false, null, null, null, null, $"File processing failed: {ex.Message}"));
            }
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Browser file import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    /// <summary>
    /// 从StringSession导入账号
    /// </summary>
    public async Task<ImportResult> ImportFromStringSessionAsync(
        string sessionString,
        int apiId,
        string apiHash,
        int? categoryId = null)
    {
        var result = await _sessionImporter.ImportFromStringSessionAsync(sessionString, apiId, apiHash);

        // 如果导入成功，保存到数据库
        if (result.Success && result.UserId.HasValue)
        {
            var account = new Account
            {
                Phone = result.Phone!,
                UserId = result.UserId.Value,
                Username = result.Username,
                SessionPath = result.SessionPath!,
                ApiId = apiId,
                ApiHash = apiHash,
                IsActive = true,
                CategoryId = categoryId,
                CreatedAt = DateTime.UtcNow,
                LastSyncAt = DateTime.UtcNow
            };

            try
            {
                await _accountManagement.CreateAccountAsync(account);
                _logger.LogInformation("Account saved to database: {Phone}", account.Phone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save account to database: {Phone}", account.Phone);
                return new ImportResult(
                    false,
                    result.Phone,
                    result.UserId,
                    result.Username,
                    result.SessionPath,
                    $"Session imported but database save failed: {ex.Message}"
                );
            }
        }

        return result;
    }
}
