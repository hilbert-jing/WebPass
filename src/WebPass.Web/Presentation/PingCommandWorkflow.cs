using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Ping;

namespace WebPass.Web.Presentation;

public sealed record ServerPingFeedback(
    Guid AssetId,
    string TargetBusinessIp,
    string Outcome,
    long? LatencyMilliseconds)
{
    public bool IsSuccess => string.Equals(
        Outcome,
        "Success",
        StringComparison.Ordinal);

    public string Summary =>
        $"Ping {UiLabels.ForPingOutcome(Outcome)} · " +
        (LatencyMilliseconds is null
            ? "无延迟数据"
            : $"{LatencyMilliseconds} ms");
}

public sealed record UnregisteredPingFeedback(
    Guid SubnetId,
    string TargetIp,
    string Outcome,
    long? LatencyMilliseconds)
{
    public bool IsSuccess => string.Equals(
        Outcome,
        "Success",
        StringComparison.Ordinal);

    public string Summary =>
        $"Ping {UiLabels.ForPingOutcome(Outcome)} · " +
        (LatencyMilliseconds is null
            ? "无延迟数据"
            : $"{LatencyMilliseconds} ms");
}

public static class PingCommandWorkflow
{
    private const string FeedbackKey = "ServerPingFeedback";
    private const string InvalidTargetMessage =
        "The Ping target is not a registered address in an enabled subnet.";
    private const string RateLimitMessage = "Ping rate limit exceeded.";
    private const string UnregisteredFeedbackKey =
        "UnregisteredPingFeedback";
    private const string InvalidUnregisteredTargetMessage =
        "The Ping target is not an unregistered address in an enabled subnet.";

    public static async Task<IActionResult> ExecuteAsync(
        PingService pingService,
        Guid assetId,
        Guid actorUserId,
        ITempDataDictionary tempData,
        Func<ServerPingFeedback, IActionResult> redirect,
        CancellationToken ct)
    {
        try
        {
            var result = await pingService.ExecuteAsync(
                assetId,
                actorUserId,
                ct);
            var feedback = new ServerPingFeedback(
                result.ServerAssetId,
                result.TargetIp,
                result.Outcome,
                result.LatencyMilliseconds);
            tempData[FeedbackKey] = JsonSerializer.Serialize(feedback);
            return redirect(feedback);
        }
        catch (UnauthorizedAccessException)
        {
            return new ForbidResult();
        }
        catch (KeyNotFoundException)
        {
            return new NotFoundObjectResult("未找到要检测的服务器。");
        }
        catch (ArgumentException)
        {
            return new BadRequestObjectResult(
                "无法检测此服务器：请求参数无效。");
        }
        catch (InvalidOperationException exception)
            when (string.Equals(
                exception.Message,
                RateLimitMessage,
                StringComparison.Ordinal))
        {
            return new ObjectResult("Ping 操作过于频繁，请稍后重试。")
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
            };
        }
        catch (InvalidOperationException exception)
            when (string.Equals(
                exception.Message,
                InvalidTargetMessage,
                StringComparison.Ordinal))
        {
            return new BadRequestObjectResult(
                "无法检测此服务器：目标无效或当前不可用。");
        }
        catch
        {
            return new ObjectResult("Ping 检测失败，请稍后重试。")
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }
    }

    public static ServerPingFeedback? TakeFeedback(
        ITempDataDictionary tempData)
    {
        if (tempData[FeedbackKey] is not string serialized)
            return null;

        try
        {
            var feedback = JsonSerializer.Deserialize<ServerPingFeedback>(
                serialized);
            if (feedback is null ||
                feedback.AssetId == Guid.Empty ||
                !IsCanonicalIpv4(feedback.TargetBusinessIp) ||
                feedback.LatencyMilliseconds < 0 ||
                feedback.Outcome is not (
                    "Success" or
                    "Timeout" or
                    "Unreachable" or
                    "InternalError"))
            {
                return null;
            }

            return feedback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static RouteValueDictionary ListRouteValues(
        ServerListQuery query) =>
        new()
        {
            ["Query.Search"] = query.Search,
            ["Query.SubnetId"] = query.SubnetId,
            ["Query.Status"] = query.Status,
            ["Query.IncludeArchived"] = query.IncludeArchived,
            ["Query.PoolMode"] = query.PoolMode,
            ["Query.Skip"] = Math.Max(0, query.Skip),
            ["Query.Take"] = Math.Clamp(query.Take, 1, 500),
        };


    public static async Task<IActionResult> ExecuteUnregisteredAsync(
        PingService pingService,
        Guid subnetId,
        string targetIp,
        Guid actorUserId,
        ITempDataDictionary tempData,
        Func<UnregisteredPingFeedback, IActionResult> redirect,
        CancellationToken ct)
    {
        try
        {
            var result = await pingService.ExecuteUnregisteredAsync(
                subnetId,
                targetIp,
                actorUserId,
                ct);
            var feedback = new UnregisteredPingFeedback(
                subnetId,
                result.TargetIp,
                result.Outcome,
                result.LatencyMilliseconds);
            tempData[UnregisteredFeedbackKey] =
                JsonSerializer.Serialize(feedback);
            return redirect(feedback);
        }
        catch (UnauthorizedAccessException)
        {
            return new ForbidResult();
        }
        catch (ArgumentException)
        {
            return new BadRequestObjectResult(
                "无法检测此 IP：请求参数无效。");
        }
        catch (InvalidOperationException exception)
            when (string.Equals(
                exception.Message,
                RateLimitMessage,
                StringComparison.Ordinal))
        {
            return new ObjectResult("Ping 操作过于频繁，请稍后重试。")
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
            };
        }
        catch (InvalidOperationException exception)
            when (string.Equals(
                exception.Message,
                InvalidUnregisteredTargetMessage,
                StringComparison.Ordinal))
        {
            return new BadRequestObjectResult(
                "无法检测此 IP：目标无效或当前不可用。");
        }
        catch
        {
            return new ObjectResult("Ping 检测失败，请稍后重试。")
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }
    }

    public static UnregisteredPingFeedback? TakeUnregisteredFeedback(
        ITempDataDictionary tempData)
    {
        if (tempData[UnregisteredFeedbackKey] is not string serialized)
            return null;

        try
        {
            var feedback =
                JsonSerializer.Deserialize<UnregisteredPingFeedback>(
                    serialized);
            if (feedback is null ||
                feedback.SubnetId == Guid.Empty ||
                !IsCanonicalIpv4(feedback.TargetIp) ||
                feedback.LatencyMilliseconds < 0 ||
                feedback.Outcome is not (
                    "Success" or
                    "Timeout" or
                    "Unreachable" or
                    "InternalError"))
            {
                return null;
            }

            return feedback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCanonicalIpv4(string? value) =>
        value is not null &&
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetwork &&
        string.Equals(
            value,
            address.ToString(),
            StringComparison.Ordinal);
}
