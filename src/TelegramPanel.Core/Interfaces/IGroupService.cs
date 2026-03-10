using TelegramPanel.Core.Models;

namespace TelegramPanel.Core.Interfaces;

/// <summary>
/// 群组服务接口
/// </summary>
public interface IGroupService
{
    /// <summary>
    /// 获取账号创建的所有群组
    /// </summary>
    Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId);

    /// <summary>
    /// 创建群组（超级群组）
    /// </summary>
    Task<GroupInfo> CreateGroupAsync(int accountId, string title, string about, bool isPublic = false, string? username = null);

    /// <summary>
    /// 获取账号当前可见的全部群组（创建者/管理员/普通成员）
    /// </summary>
    Task<List<GroupInfo>> GetVisibleGroupsAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取群组详情
    /// </summary>
    Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId);

    /// <summary>
    /// 退出群组。
    /// </summary>
    Task<bool> LeaveGroupAsync(int accountId, long groupId);

    /// <summary>
    /// 解散群组（超级群需要创建者权限；基础群暂不支持）。
    /// </summary>
    Task<bool> DisbandGroupAsync(int accountId, long groupId);

    /// <summary>
    /// 导出加入链接：公开群组返回 t.me 链接；否则导出邀请链接。
    /// </summary>
    Task<string> ExportJoinLinkAsync(int accountId, long groupId);

    /// <summary>
    /// 获取群组管理员列表（需要权限）
    /// </summary>
    Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId);

    /// <summary>
    /// 更新群组标题与简介。
    /// </summary>
    Task<bool> UpdateGroupInfoAsync(int accountId, long groupId, string title, string? about);

    /// <summary>
    /// 设置群组公开用户名或切回私密。
    /// </summary>
    Task<bool> SetGroupVisibilityAsync(int accountId, long groupId, bool isPublic, string? username = null);

    /// <summary>
    /// 设置群组头像。
    /// </summary>
    Task<bool> SetGroupPhotoAsync(int accountId, long groupId, Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
