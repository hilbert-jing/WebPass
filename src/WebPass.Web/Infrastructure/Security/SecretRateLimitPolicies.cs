using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WebPass.Web.Infrastructure.Security;

public static class SecretRateLimitPolicies
{
    public const string Reauthentication = "SecretReauthentication";
    public const string Reveal = "SecretReveal";

    public static void AddTo(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Reauthentication, context =>
            FixedWindow(context, permitLimit: 5));
        options.AddPolicy(Reveal, context =>
            FixedWindow(context, permitLimit: 10));
    }

    private static RateLimitPartition<string> FixedWindow(
        HttpContext context,
        int permitLimit) =>
        !HttpMethods.IsPost(context.Request.Method)
            ? RateLimitPartition.GetNoLimiter($"browse:{PartitionKey(context)}")
            : RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
            });

    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
