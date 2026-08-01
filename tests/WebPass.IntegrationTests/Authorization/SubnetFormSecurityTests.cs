using System.Globalization;
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
using WebPass.Web.Pages;
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
    public async Task Valid_antiforgery_post_reaches_the_named_preview_handler_with_the_posted_cidr()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync(
            "/subnets?handler=Preview",
            new FormUrlEncodedContent(Form(token)));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"networkAddress\":\"10.20.30.0\"", body, StringComparison.Ordinal);
        Assert.Contains("\"broadcastAddress\":\"10.20.30.255\"", body, StringComparison.Ordinal);
        Assert.Contains("\"usableAddressCount\":254", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected_before_the_named_preview_handler()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync(
            "/subnets?handler=Preview",
            new FormUrlEncodedContent(Form(null)));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("networkAddress", body, StringComparison.Ordinal);
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

    [Fact]
    public async Task Browser_checkbox_payload_does_not_silently_disable_an_enabled_subnet_during_edit()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var subnet = (await factory.GetSubnetsAsync()).Single();
        var html = await client.GetStringAsync("/subnets");
        var editForm = Regex.Match(
            html,
            "<form[^>]*action=\"/subnets\\?handler=Edit\"[^>]*>.*?</form>",
            RegexOptions.Singleline).Value;
        var enabledInputs = Regex.Matches(
            editForm,
            "<input[^>]*name=\"Input.IsEnabled\"[^>]*>",
            RegexOptions.Singleline);
        var form = Form(token);
        form.RemoveAll(field => field.Key == "Input.IsEnabled");
        foreach (Match input in enabledInputs)
        {
            var type = Regex.Match(input.Value, "type=\"([^\"]+)\"").Groups[1].Value;
            if (type == "checkbox" &&
                !input.Value.Contains("checked", StringComparison.Ordinal))
            {
                continue;
            }

            var value = Regex.Match(input.Value, "value=\"([^\"]+)\"").Groups[1].Value;
            form.Add(new KeyValuePair<string, string>("Input.IsEnabled", value));
        }
        form.Add(new KeyValuePair<string, string>("id", subnet.Id.ToString()));
        form.Add(new KeyValuePair<string, string>(
            "rowVersion",
            Convert.ToBase64String(subnet.RowVersion)));

        var response = await client.PostAsync(
            "/subnets?handler=Edit",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await factory.GetSubnetsAsync()).Single().IsEnabled);
    }

    [Fact]
    public async Task Invalid_preview_keeps_bad_request_status_and_returns_an_actionable_error()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var form = Form(token);
        form.RemoveAll(field => field.Key == "Input.Cidr");
        form.Add(new KeyValuePair<string, string>("Input.Cidr", "not-a-cidr"));

        var response = await client.PostAsync(
            "/subnets?handler=Preview",
            new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "\"error\":\"网段信息无效，请检查 CIDR 和必填字段。\"",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_create_fields_render_chinese_required_errors_without_creating_a_subnet()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var form = Form(token);
        form.RemoveAll(field => field.Key is "Input.Name" or "Input.Cidr" or "Input.Location");
        form.Add(new KeyValuePair<string, string>("Input.Name", ""));
        form.Add(new KeyValuePair<string, string>("Input.Cidr", ""));
        form.Add(new KeyValuePair<string, string>("Input.Location", ""));

        var response = await client.PostAsync(
            "/subnets?handler=Create",
            new FormUrlEncodedContent(form));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("请输入网段名称。", html, StringComparison.Ordinal);
        Assert.Contains("请输入 CIDR。", html, StringComparison.Ordinal);
        Assert.Contains("请输入位置。", html, StringComparison.Ordinal);
        Assert.Empty(await factory.GetSubnetsAsync());
    }

    [Fact]
    public async Task Overlapping_create_renders_an_actionable_error_without_changing_the_page_status()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        await factory.AddSubnetAsync("10.20.30.0/24", "10.20.30.0");
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync(
            "/subnets?handler=Create",
            new FormUrlEncodedContent(Form(token)));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("无法保存网段：该范围与现有网段重叠。", html, StringComparison.Ordinal);
        Assert.Single(await factory.GetSubnetsAsync());
    }

    [Fact]
    public async Task Shrinking_past_an_asset_renders_an_actionable_error_without_changing_the_page_status()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        await factory.AddSubnetAsync(withAsset: true);
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var subnet = (await factory.GetSubnetsAsync()).Single();
        var form = Form(token);
        form.RemoveAll(field => field.Key == "Input.Cidr");
        form.Add(new KeyValuePair<string, string>("Input.Cidr", "10.0.0.0/30"));
        form.Add(new KeyValuePair<string, string>("id", subnet.Id.ToString()));
        form.Add(new KeyValuePair<string, string>("rowVersion", Convert.ToBase64String(subnet.RowVersion)));

        var response = await client.PostAsync(
            "/subnets?handler=Edit",
            new FormUrlEncodedContent(form));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("无法缩小网段：已有服务器地址将落在新范围之外。", html, StringComparison.Ordinal);
        Assert.Equal("10.0.0.0/24", (await factory.GetSubnetsAsync()).Single().Cidr);
    }

    [Fact]
    public async Task Deleting_a_subnet_with_assets_renders_an_actionable_error_without_changing_the_page_status()
    {
        using var factory = NewFactory(PermissionCode.SubnetManage);
        factory.InitializeData();
        await factory.AddSubnetAsync(withAsset: true);
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var subnet = (await factory.GetSubnetsAsync()).Single();
        var form = Form(token);
        form.Add(new KeyValuePair<string, string>("id", subnet.Id.ToString()));
        form.Add(new KeyValuePair<string, string>("rowVersion", Convert.ToBase64String(subnet.RowVersion)));

        var response = await client.PostAsync(
            "/subnets?handler=Delete",
            new FormUrlEncodedContent(form));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("无法删除网段：请先解除关联服务器，或停用该网段。", html, StringComparison.Ordinal);
        Assert.Single(await factory.GetSubnetsAsync());
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
            var identity = new ClaimsIdentity(
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        User.Id.ToString()),
                    new Claim(
                        LoginModel.SessionStartedClaimType,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            .ToString(CultureInfo.InvariantCulture)),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
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

        public async Task AddSubnetAsync(
            string cidr = "10.0.0.0/24",
            string networkAddress = "10.0.0.0",
            bool withAsset = false)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var subnet = new Subnet
            {
                Name = "Existing",
                Cidr = cidr,
                NetworkAddress = networkAddress,
                PrefixLength = 24,
                Location = "HQ",
                RowVersion = [1],
            };
            db.Subnets.Add(subnet);
            if (withAsset)
            {
                db.ServerAssets.Add(new ServerAsset
                {
                    SubnetId = subnet.Id,
                    BusinessIp = "10.0.0.9",
                    BusinessIpNumber = 167772169,
                    Location = "HQ",
                    ComputerName = "web-09",
                    SystemName = "WebPass",
                });
            }
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
