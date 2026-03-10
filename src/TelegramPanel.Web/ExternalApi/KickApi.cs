using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.ExternalApi;

public static class KickApi
{
    public static void MapKickApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/kick", HandleAsync)
            .DisableAntiforgery()
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IConfiguration configuration,
        BotManagementService botManagement,
        BatchTaskManagementService taskManagement,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ExternalApi.Kick");
        var providedKey = http.Request.Headers["X-API-Key"].ToString();

        var apis = configuration.GetSection("ExternalApi:Apis").Get<List<ExternalApiDefinition>>() ?? new List<ExternalApiDefinition>();
        var kickApis = apis.Where(a => string.Equals(a.Type, ExternalApiTypes.Kick, StringComparison.OrdinalIgnoreCase)).ToList();
        var matched = kickApis.FirstOrDefault(a => a.Enabled && FixedTimeEquals(a.ApiKey, providedKey));
        if (matched == null)
        {
            var anyEnabled = kickApis.Any(a => a.Enabled);
            if (!anyEnabled)
            {
                return Results.NotFound(new KickResponse(
                    false,
                    "该接口未启用（请先在面板「API 管理」创建并启用一个 /api/kick 配置）",
                    new KickSummary(0, 0, 0),
                    Array.Empty<KickResultItem>()));
            }

            return Results.Json(
                new KickResponse(false, "X-API-Key 无效", new KickSummary(0, 0, 0), Array.Empty<KickResultItem>()),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        matched.Kick ??= new KickApiDefinition();
        matched.Kick.ChatIds ??= new List<long>();

        KickRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<KickRequest>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Results.BadRequest(new KickResponse(false, $"请求体 JSON 无效：{ex.Message}", new KickSummary(0, 0, 0), Array.Empty<KickResultItem>()));
        }

        if (request == null)
            return Results.BadRequest(new KickResponse(false, "请求体不能为空", new KickSummary(0, 0, 0), Array.Empty<KickResultItem>()));

        if (request.UserId <= 0)
            return Results.BadRequest(new KickResponse(false, "user_id 无效", new KickSummary(0, 0, 0), Array.Empty<KickResultItem>()));

        var permanentBan = request.PermanentBan ?? matched.Kick.PermanentBanDefault;
        var configuredBotId = matched.Kick.BotId;
        var useAllChats = configuredBotId == 0 ? true : matched.Kick.UseAllChats;
        var configuredChatSet = new HashSet<long>(matched.Kick.ChatIds.Where(x => x != 0));

        var targets = await ResolveTargetsAsync(botManagement, configuredBotId, useAllChats, configuredChatSet, cancellationToken);
        if (targets.TotalChats == 0)
        {
            return Results.BadRequest(new KickResponse(
                false,
                "未配置任何可操作的频道/群组（请先在页面选择机器人与频道/群组）",
                new KickSummary(0, 0, 0),
                    Array.Empty<KickResultItem>()));
        }

        // 提交到任务中心，由后台执行（避免反代/CF 524 超时）
        var createdTask = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.ExternalApiKick,
            Total = targets.TotalChats,
            Completed = 0,
            Failed = 0,
            Config = SerializeIndented(new KickTaskLog
            {
                ApiName = matched.Name ?? "",
                BotId = configuredBotId,
                UseAllChats = useAllChats,
                ChatIds = configuredChatSet.OrderBy(x => x).ToList(),
                UserId = request.UserId,
                PermanentBan = permanentBan,
                RequestedAtUtc = DateTime.UtcNow
            })
        });

        logger.LogInformation("Kick task queued: taskId={TaskId} api={ApiName} botId={BotId} total={Total} userId={UserId} permanent={Permanent}",
            createdTask.Id, matched.Name ?? "", configuredBotId, targets.TotalChats, request.UserId, permanentBan);

        return Results.Json(
            new KickResponse(
                Success: true,
                Message: $"已提交后台任务 #{createdTask.Id}，请到面板【任务中心】查看进度与结果。",
                Summary: new KickSummary(targets.TotalChats, 0, 0),
                Results: Array.Empty<KickResultItem>(),
                TaskId: createdTask.Id),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<TargetResolution> ResolveTargetsAsync(
        BotManagementService botManagement,
        int configuredBotId,
        bool useAllChats,
        HashSet<long> configuredChatIds,
        CancellationToken cancellationToken)
    {
        var bots = (await botManagement.GetAllBotsAsync())
            .Where(b => b.IsActive)
            .OrderBy(b => b.Id)
            .ToList();

        if (configuredBotId > 0)
            bots = bots.Where(b => b.Id == configuredBotId).ToList();

        var groups = new List<TargetGroup>();
        var totalChats = 0;

        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chats = (await botManagement.GetChatsAsync(bot.Id)).ToList();
            if (!useAllChats && configuredChatIds.Count > 0)
                chats = chats.Where(c => configuredChatIds.Contains(c.TelegramId)).ToList();

            if (chats.Count == 0)
                continue;

            groups.Add(new TargetGroup(bot, chats));
            totalChats += chats.Count;
        }

        return new TargetResolution(groups, totalChats);
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        expected = (expected ?? string.Empty).Trim();
        provided = (provided ?? string.Empty).Trim();

        if (expected.Length == 0 || provided.Length == 0)
            return false;

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        if (a.Length != b.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string SerializeIndented<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record TargetGroup(Bot Bot, List<BotChannel> Chats);
    private sealed record TargetResolution(List<TargetGroup> Groups, int TotalChats);

}
