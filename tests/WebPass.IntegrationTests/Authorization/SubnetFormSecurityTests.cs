using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using Xunit;

namespace WebPass.IntegrationTests.Authorization;

public sealed class SubnetFormSecurityTests
{
    [Fact]
    public async Task Rendered_subnet_create_form_has_a_named_handler_url_and_antiforgery_token()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/subnets");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/subnets?handler=Create\"", html, StringComparison.Ordinal);
        Assert.Matches("name=\"__RequestVerificationToken\"", html);
    }

    [Fact]
    public async Task Valid_antiforgery_post_reaches_the_named_create_handler()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync("/subnets?handler=Create", new FormUrlEncodedContent(Form(token)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("10.20.30.0/24", (await factory.GetSubnetsAsync()).Single().Cidr);
    }

    [Fact]
    public async Task Missing_or_invalid_antiforgery_token_is_rejected_before_the_handler()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        await GetAntiforgeryTokenAsync(client);

        var missing = await client.PostAsync("/subnets?handler=Create", new FormUrlEncodedContent(Form(null)));
        var invalid = await client.PostAsync("/subnets?handler=Create", new FormUrlEncodedContent(Form("not-a-valid-token")));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await factory.GetSubnetsAsync());
    }

    [Fact]
    public async Task Same_authenticated_cookie_is_forbidden_after_permission_is_revoked()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(handleCookies: false);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/subnets")).StatusCode);

        await factory.ReplacePermissionsAsync([]);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/subnets")).StatusCode);
    }

    [Fact]
    public async Task Same_authenticated_cookie_is_forbidden_after_user_is_disabled()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(handleCookies: false);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/subnets")).StatusCode);

        await factory.DisableUserAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/subnets")).StatusCode);
    }

    [Fact]
    public async Task Malformed_row_version_post_is_rejected_without_changing_a_subnet()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var subnet = (await factory.GetSubnetsAsync()).Single();

        var form = Form(token);
        form.Add(new KeyValuePair<string, string>("id", subnet.Id.ToString()));
        form.Add(new KeyValuePair<string, string>("isEnabled", "false"));
        form.Add(new KeyValuePair<string, string>("rowVersion", "not-base64"));
        var response = await client.PostAsync("/subnets?handler=SetEnabled", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await factory.GetSubnetsAsync()).Single().IsEnabled);
    }

    private static SubnetCookieFactory NewFactory(params string[] permissions) => new(NewUser(), permissions);
    private static AppUser NewUser() => new() { Username = Guid.NewGuid().ToString("N"), PasswordHash = "hash" };

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/subnets");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static List<KeyValuePair<string, string>> Form(string? token) =>
    [
        new("Input.Name", "Operations"),
        new("Input.Cidr", "10.20.30.0/24"),
        new("Input.Location", "HQ"),
        new("Input.Notes", ""),
        new("Input.IsEnabled", "true"),
        .. (token is null ? [] : new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token) }),
    ];

    private sealed class SubnetCookieFactory(AppUser user, params string[] permissions) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        private readonly string[] _permissions = permissions;
        public AppUser User { get; } = user;

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Users.Add(User);
            db.UserPermissions.AddRange(_permissions.Select(code => new UserPermission { UserId = User.Id, PermissionCode = code }));
            db.SaveChanges();
        }

        public HttpClient CreateAuthenticatedClient(bool handleCookies = true)
        {
            using var scope = Services.CreateScope();
            var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.Id.ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = handleCookies });
            client.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}");
            return client;
        }

        public async Task ReplacePermissionsAsync(string[] codes)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.UserPermissions.RemoveRange(db.UserPermissions.Where(x => x.UserId == User.Id));
            db.UserPermissions.AddRange(codes.Select(code => new UserPermission { UserId = User.Id, PermissionCode = code }));
            await db.SaveChangesAsync();
        }

        public async Task DisableUserAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            (await db.Users.SingleAsync(x => x.Id == User.Id)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        public async Task AddSubnetAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Subnets.Add(new Subnet { Name = "Existing", Cidr = "10.0.0.0/24", NetworkAddress = "10.0.0.0", PrefixLength = 24, Location = "HQ" });
            await db.SaveChangesAsync();
        }

        public async Task<List<Subnet>> GetSubnetsAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().Subnets.AsNoTracking().ToListAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebPassDbContext>>();
            services.RemoveAll<WebPassDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
            services.AddDbContext<WebPassDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
