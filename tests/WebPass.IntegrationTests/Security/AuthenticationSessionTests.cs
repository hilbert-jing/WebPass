using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Security;

public sealed class AuthenticationSessionTests
{
    [Fact]
    public void Cookie_uses_thirty_minute_sliding_expiration()
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);

        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Fact]
    public async Task Ticket_younger_than_eight_hours_remains_valid()
    {
        using var factory = new WebPassFactory();
        using var scope = factory.Services.CreateScope();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            scope.ServiceProvider,
            options,
            DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
    }

    [Fact]
    public async Task Ticket_at_least_eight_hours_old_is_rejected()
    {
        using var factory = new WebPassFactory();
        using var scope = factory.Services.CreateScope();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            scope.ServiceProvider,
            options,
            DateTimeOffset.UtcNow.AddHours(-8).AddMinutes(-1)
                .ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-time")]
    [InlineData("253402300800")]
    public async Task Missing_or_invalid_session_start_is_rejected(
        string? value)
    {
        using var factory = new WebPassFactory();
        using var scope = factory.Services.CreateScope();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            scope.ServiceProvider,
            options,
            value);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task Future_session_start_is_rejected()
    {
        using var factory = new WebPassFactory();
        using var scope = factory.Services.CreateScope();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            scope.ServiceProvider,
            options,
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task Successful_login_writes_original_session_start_claim()
    {
        await using var db = NewDatabase();
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("correct-password"),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var authentication = new RecordingAuthenticationService();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        var model = new LoginModel(new LoginService(db, hasher))
        {
            Input = new LoginModel.LoginInput
            {
                Username = "operator",
                Password = "correct-password",
            },
            PageContext = new PageContext
            {
                HttpContext = httpContext,
            },
        };
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.IsType<RedirectResult>(
            await model.OnPostAsync(default));

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var value = authentication.SignedInPrincipal!
            .FindFirstValue(LoginModel.SessionStartedClaimType);
        Assert.True(long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var started));
        Assert.InRange(started, before, after);
    }

    [Fact]
    public async Task Logout_only_accepts_post_writes_audit_and_signs_out()
    {
        await using var db = NewDatabase();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = "opaque-hash",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var authentication = new RecordingAuthenticationService();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var model = new LogoutModel(new AuditWriter(db))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(
                                ClaimTypes.NameIdentifier,
                                user.Id.ToString()),
                        ],
                        CookieAuthenticationDefaults
                            .AuthenticationScheme)),
                },
            },
        };
        var tempData = new TempDataDictionary(
            model.HttpContext,
            new DictionaryTempDataProvider());
        model.TempData = tempData;

        Assert.Equal(
            StatusCodes.Status405MethodNotAllowed,
            Assert.IsType<StatusCodeResult>(model.OnGet()).StatusCode);
        var result = await model.OnPostAsync(default);

        Assert.Equal(
            "/login",
            Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.SignedOutScheme);
        Assert.Equal("已安全退出。", tempData["StatusMessage"]);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("Logout", audit.Action);
        Assert.Null(audit.Details);
    }

    private static CookieAuthenticationOptions CookieOptions(
        IServiceProvider services) =>
        services
            .GetRequiredService<
                IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

    private static CookieValidatePrincipalContext NewCookieContext(
        IServiceProvider services,
        CookieAuthenticationOptions options,
        string? started)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (started is not null)
        {
            claims.Add(new(
                LoginModel.SessionStartedClaimType,
                started));
        }

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme,
            typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme)),
            scheme.Name);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        return new CookieValidatePrincipalContext(
            httpContext,
            scheme,
            options,
            ticket);
    }

    private static WebPassDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }

    private sealed class RecordingAuthenticationService
        : IAuthenticationService
    {
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }
        public string? SignedOutScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            SignedOutScheme = scheme;
            return Task.CompletedTask;
        }
    }
}
