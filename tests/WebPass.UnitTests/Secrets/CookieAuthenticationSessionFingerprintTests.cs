using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class CookieAuthenticationSessionFingerprintTests
{
    [Fact]
    public void Current_cookie_is_represented_by_a_stable_non_reversible_fingerprint()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".WebPass.Auth=private-cookie-value";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var options = new CookieAuthenticationOptions();
        options.Cookie.Name = ".WebPass.Auth";
        var fingerprints = new CookieAuthenticationSessionFingerprint(
            accessor,
            new StubOptionsMonitor<CookieAuthenticationOptions>(options));

        var first = fingerprints.GetCurrent();
        var second = fingerprints.GetCurrent();

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first);
        Assert.DoesNotContain("private-cookie-value", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_authentication_cookie_is_rejected()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var fingerprints = new CookieAuthenticationSessionFingerprint(
            accessor,
            new StubOptionsMonitor<CookieAuthenticationOptions>(new CookieAuthenticationOptions()));

        Assert.Throws<UnauthorizedAccessException>(() => fingerprints.GetCurrent());
    }

    private sealed class StubOptionsMonitor<T>(T current) : IOptionsMonitor<T>
    {
        public T CurrentValue => current;
        public T Get(string? name) => current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
