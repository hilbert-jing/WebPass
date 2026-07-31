using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WebPass.Web.Application.Ping;

namespace WebPass.Web.Presentation;

public sealed record ServerPingFeedback(
    Guid AssetId,
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

    public static async Task<IActionResult> ExecuteAsync(
        PingService pingService,
        Guid assetId,
        Guid actorUserId,
        ITempDataDictionary tempData,
        Func<IActionResult> redirect,
        CancellationToken ct)
    {
        try
        {
            var result = await pingService.ExecuteAsync(
                assetId,
                actorUserId,
                ct);
            tempData[FeedbackKey] = JsonSerializer.Serialize(
                new ServerPingFeedback(
                    result.ServerAssetId,
                    result.Outcome,
                    result.LatencyMilliseconds));
            return redirect();
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
}
