using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Core;

/// <summary>
/// Core层服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramPanelCore(this IServiceCollection services)
    {
        // 注册 Telegram 客户端池（单例）
        services.AddSingleton<ITelegramClientPool, TelegramClientPool>();

        // 注册 Telegram 操作服务
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<ISessionImporter, SessionImporter>();

        // 注册账号导入协调服务
        services.AddScoped<AccountImportService>();

        // 注册数据管理服务
        services.AddScoped<AccountManagementService>();
        services.AddScoped<ChannelManagementService>();
        services.AddScoped<GroupManagementService>();
        services.AddScoped<BatchTaskManagementService>();

        return services;
    }
}
