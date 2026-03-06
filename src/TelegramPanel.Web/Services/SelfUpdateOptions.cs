namespace TelegramPanel.Web.Services;

public sealed class SelfUpdateOptions
{
    /// <summary>
    /// 是否启用应用内一键更新
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 仅允许在 Docker 容器内执行更新
    /// </summary>
    public bool DockerOnly { get; set; } = true;

    /// <summary>
    /// 更新工作根目录。为空时优先使用 /data，不存在则回退到 ContentRoot。
    /// </summary>
    public string WorkRootPath { get; set; } = "";

    /// <summary>
    /// 更新包下载与解压的临时目录名（位于 WorkRootPath 下）
    /// </summary>
    public string WorkDirectoryName { get; set; } = "self-update";

    /// <summary>
    /// 当前版本目录名（位于 WorkRootPath 下）
    /// </summary>
    public string CurrentDirectoryName { get; set; } = "app-current";

    /// <summary>
    /// 旧版本备份目录名（位于 WorkRootPath 下）
    /// </summary>
    public string BackupDirectoryName { get; set; } = "app-previous";

    /// <summary>
    /// 下载更新包超时秒数
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 更新完成后，触发应用重启的延迟秒数
    /// </summary>
    public int RestartDelaySeconds { get; set; } = 2;
}
