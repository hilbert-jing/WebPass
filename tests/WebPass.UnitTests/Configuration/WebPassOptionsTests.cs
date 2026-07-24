using Xunit;
using WebPass.Web.Configuration;

namespace WebPass.UnitTests.Configuration;

public sealed class WebPassOptionsTests
{
    [Fact]
    public void Rejects_non_positive_ping_timeout()
    {
        var result = new WebPassOptionsValidator().Validate(null,
            new WebPassOptions { PingTimeoutMilliseconds = 0, PingMaxConcurrency = 2, PingPerUserPerMinute = 5 });

        Assert.False(result.Succeeded);
    }
}
