using Microsoft.Extensions.Options;

namespace WebPass.Web.Configuration;

public sealed class WebPassOptions
{
    public const string SectionName = "WebPass";

    public int PingTimeoutMilliseconds { get; init; }

    public int PingMaxConcurrency { get; init; }

    public int PingPerUserPerMinute { get; init; }
}

public sealed class WebPassOptionsValidator : IValidateOptions<WebPassOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPassOptions value) =>
        value.PingTimeoutMilliseconds > 0 && value.PingMaxConcurrency > 0 && value.PingPerUserPerMinute > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Ping values must be positive.");
}
