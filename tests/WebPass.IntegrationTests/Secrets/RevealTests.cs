using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Secrets;

public sealed class RevealTests
{
    [Fact]
    public async Task Reveal_without_reauthentication_grant_returns_forbidden()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await RevealAsync(client, factory.AssetId, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reauthenticate_then_reveal_returns_no_store_json()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var reauthenticated = await client.PostAsync(
            "/secrets/reauthenticate",
            new FormUrlEncodedContent([
                new("Input.Password", "current-password"),
                new("__RequestVerificationToken", token),
            ]));
        Assert.Equal(HttpStatusCode.Redirect, reauthenticated.StatusCode);

        var response = await RevealAsync(client, factory.AssetId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
        var result = await response.Content.ReadFromJsonAsync<RevealResult>();
        Assert.Equal("server-password", result?.Password);
    }

    [Fact]
    public async Task Reveal_post_without_antiforgery_token_is_rejected()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            $"/secrets/reveal?assetId={factory.AssetId}",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_get_is_not_an_allowed_operation()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/secrets/reveal?assetId={factory.AssetId}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Servers_page_exposes_reveal_control_without_rendering_the_password()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(
            "/servers?Query.Search=ERP&Query.Status=Unknown&Query.IncludeArchived=true&Query.Skip=0&Query.Take=25");

        Assert.Contains($"data-asset-id=\"{factory.AssetId}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-reveal", html, StringComparison.Ordinal);
        Assert.Contains($"id=\"secret-{factory.AssetId}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-panel", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-value", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-countdown", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-status", html, StringComparison.Ordinal);
        Assert.Contains("data-secret-copy", html, StringComparison.Ordinal);
        Assert.Contains("服务器密码将在 30 秒后自动隐藏", html, StringComparison.Ordinal);
        Assert.Contains("/js/secret-reveal.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("server-password", html, StringComparison.Ordinal);
        var returnUrlMatch = Regex.Match(html, "data-return-url=\"([^\"]+)\"");
        Assert.True(returnUrlMatch.Success);
        var returnUrl = new Uri(client.BaseAddress!, WebUtility.HtmlDecode(returnUrlMatch.Groups[1].Value));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(returnUrl.Query);
        Assert.Equal("ERP", query["Query.Search"]);
        Assert.Equal("Unknown", query["Query.Status"]);
        Assert.Equal("True", query["Query.IncludeArchived"]);
        Assert.Equal("False", query["Query.PoolMode"]);
        Assert.Equal("0", query["Query.Skip"]);
        Assert.Equal("25", query["Query.Take"]);
        Assert.Equal($"#server-{factory.AssetId}", returnUrl.Fragment);
        Assert.Contains($"id=\"server-{factory.AssetId}\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reauthentication_preserves_list_query_and_row_anchor_including_after_failure(
        bool failFirst)
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var returnUrl = $"/servers?Query.Search=%E6%9C%BA%E6%88%BF%20A%26B&Query.SubnetId={Guid.NewGuid()}&Query.Status=Unknown&Query.IncludeArchived=True&Query.PoolMode=True&Query.Skip=50&Query.Take=25#server-{factory.AssetId}";
        var html = await client.GetStringAsync(
            $"/secrets/reauthenticate?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(returnUrl, ReturnUrlInput(html));
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        if (failFirst)
        {
            using var failed = await client.PostAsync("/secrets/reauthenticate", new FormUrlEncodedContent(
            [
                new("Input.Password", "wrong-password"),
                new("ReturnUrl", ReturnUrlInput(html)),
                new("__RequestVerificationToken", token),
            ]));
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            html = await failed.Content.ReadAsStringAsync();
            Assert.Equal(returnUrl, ReturnUrlInput(html));
        }

        using var response = await client.PostAsync("/secrets/reauthenticate", new FormUrlEncodedContent(
        [
            new("Input.Password", "current-password"),
            new("ReturnUrl", ReturnUrlInput(html)),
            new("__RequestVerificationToken", token),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(returnUrl, response.Headers.Location!.OriginalString);
    }

    private static string ReturnUrlInput(string html) => WebUtility.HtmlDecode(
        Regex.Match(html, "name=\"ReturnUrl\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);

    [Fact]
    public async Task Viewing_reauthentication_page_does_not_consume_sensitive_post_budget()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/secrets/reauthenticate");
        Assert.Contains("验证当前密码", html, StringComparison.Ordinal);

        var responses = new List<HttpResponseMessage>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            responses.Add(await client.GetAsync("/secrets/reauthenticate"));
        }

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Sixth_reauthentication_post_within_a_minute_is_rate_limited()
    {
        using var factory = new RevealFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var token = await GetAntiforgeryTokenAsync(client);

        var responses = new List<HttpResponseMessage>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            responses.Add(await client.PostAsync(
                "/secrets/reauthenticate",
                new FormUrlEncodedContent([
                    new("Input.Password", "wrong-password"),
                    new("__RequestVerificationToken", token),
                ])));
        }

        Assert.All(
            responses.Take(5),
            response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[5].StatusCode);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/secrets/reauthenticate");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> RevealAsync(
        HttpClient client,
        Guid assetId,
        string token) =>
        client.PostAsync(
            $"/secrets/reveal?assetId={assetId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

    private sealed class RevealFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid AssetId { get; } = Guid.NewGuid();

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new AppUser
            {
                Id = UserId,
                Username = "operator",
                PasswordHash = hasher.Hash("current-password"),
                RowVersion = [1, 2, 3],
            });
            db.UserPermissions.Add(new UserPermission
            {
                UserId = UserId,
                PermissionCode = PermissionCode.SecretReveal,
            });
            db.UserPermissions.Add(new UserPermission
            {
                UserId = UserId,
                PermissionCode = PermissionCode.AssetView,
            });
            db.ServerAssets.Add(new ServerAsset
            {
                Id = AssetId,
                BusinessIp = "10.0.0.10",
                Location = "DC",
                ComputerName = "server-10",
                SystemName = "ERP",
            });
            db.DataEncryptionKeys.Add(new DataEncryptionKey
            {
                KeyVersion = 1,
                WrappedKey = [1],
                CertificateThumbprint = "thumbprint",
            });
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = AssetId,
                Ciphertext = [1],
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
            });
            db.SaveChanges();
        }

        public HttpClient CreateAuthenticatedClient()
        {
            using var scope = Services.CreateScope();
            var cookieOptions = scope.ServiceProvider
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity(
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        UserId.ToString()),
                    new Claim(
                        LoginModel.SessionStartedClaimType,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            .ToString(CultureInfo.InvariantCulture)),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                CookieAuthenticationDefaults.AuthenticationScheme);
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            client.DefaultRequestHeaders.Add(
                "Cookie",
                $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<WebPassDbContext>>();
                services.RemoveAll<WebPassDbContext>();
                services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
                services.AddDbContext<WebPassDbContext>(
                    options => options.UseInMemoryDatabase(_databaseName));
                services.RemoveAll<ISecretCipher>();
                services.AddSingleton<ISecretCipher, StubSecretCipher>();
            });
    }

    private sealed class StubSecretCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            Task.FromResult("server-password");
    }
}
