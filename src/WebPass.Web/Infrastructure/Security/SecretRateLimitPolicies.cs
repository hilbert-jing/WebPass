using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WebPass.Web.Infrastructure.Security;

public static class SecretRateLimitPolicies
{
    public const string Login = "Login";
    public const string Reauthentication = "SecretReauthentication";
    public const string Ping = "Ping";
    public const string Reveal = "SecretReveal";

    public static void AddTo(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static async (context, ct) =>
        {
            var path = context.HttpContext.Request.Path.Value;
            if (path is not null &&
                path.StartsWith("/servers/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await context.HttpContext.Response.WriteAsync(
                    "Ping 操作过于频繁，请稍后重试。",
                    ct);
            }
        };
        options.AddPolicy(Login, context =>
            FixedWindow(context, permitLimit: 5, useUserIdentity: false));
        options.AddPolicy(Reauthentication, context =>
            FixedWindow(context, permitLimit: 5, useUserIdentity: true));
        options.AddPolicy(Ping, context =>
            FixedWindow(context, permitLimit: 5, useUserIdentity: true));
        options.AddPolicy(Reveal, context =>
            FixedWindow(context, permitLimit: 10, useUserIdentity: true));
    }

    private static RateLimitPartition<string> FixedWindow(
        HttpContext context,
        int permitLimit,
        bool useUserIdentity) =>
        !HttpMethods.IsPost(context.Request.Method)
            ? RateLimitPartition.GetNoLimiter($"browse:{PartitionKey(context, useUserIdentity)}")
            : RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context, useUserIdentity),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = 0,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                });

    private static string PartitionKey(HttpContext context, bool useUserIdentity) =>
        (useUserIdentity ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
