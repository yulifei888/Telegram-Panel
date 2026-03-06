using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using TelegramPanel.Modules;
using TelegramPanel.Web.Components.ModulePages;
using TelegramPanel.Web.ExternalApi;

namespace TelegramPanel.Web.Modules.BuiltIn;

public sealed class KickApiModule : ITelegramPanelModule, IModuleApiProvider, IModuleUiProvider
{
    public KickApiModule(string version)
    {
        Manifest = new ModuleManifest
        {
            Id = "builtin.kick-api",
            Name = "外部 API：踢人/封禁",
            Version = version,
            Host = new HostCompatibility(),
            Entry = new ModuleEntryPoint { Assembly = "", Type = typeof(KickApiModule).FullName ?? "" }
        };
    }

    public ModuleManifest Manifest { get; }

    public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
    {
        services.AddScoped<IModuleTaskHandler, ExternalApiKickTaskHandler>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
    {
        if (!endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("ExternalApi:Enabled", true))
        {
            // 预留开关；默认开启
        }

        endpoints.MapKickApi();
    }

    public IEnumerable<ModuleApiTypeDefinition> GetApis(ModuleHostContext context)
    {
        yield return new ModuleApiTypeDefinition
        {
            Type = ExternalApiTypes.Kick,
            DisplayName = "踢人/封禁",
            Route = "/api/kick",
            Description = "从配置的 Bot 管理的频道/群组中踢出或封禁指定用户（按 X-API-Key 匹配配置项）。",
            Order = 10
        };
    }

    public IEnumerable<ModuleNavItem> GetNavItems(ModuleHostContext context)
    {
        yield break;
    }

    public IEnumerable<ModulePageDefinition> GetPages(ModuleHostContext context)
    {
        yield return new ModulePageDefinition
        {
            Key = "kick",
            Title = "踢人/封禁",
            Icon = Icons.Material.Filled.PersonRemove,
            Group = "外部 API",
            Order = 10,
            ComponentType = typeof(KickApiPage).AssemblyQualifiedName ?? ""
        };
    }
}
