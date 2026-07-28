using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.IntegrationTests.Security;

public sealed class ProductionSecurityTests
{
    [Fact]
    public async Task Servers_page_sets_restrictive_security_headers()
    {
        using var factory = new ProductionSecurityFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/servers");

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Health_reports_only_application_and_database_availability()
    {
        using var factory = new ProductionSecurityFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(["application", "database"], properties.Select(x => x.Name).Order().ToArray());
        Assert.All(properties, property => Assert.Equal("available", property.Value.GetString()));
    }

    [Fact]
    public async Task Production_http_request_redirects_to_https()
    {
        using var factory = new ProductionSecurityFactory("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
        });

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https", response.Headers.Location?.Scheme);
    }

    [Fact]
    public async Task Sixth_unknown_user_login_post_within_a_minute_is_rate_limited()
    {
        using var factory = new ProductionSecurityFactory();
        using var client = factory.CreateHttpsClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var responses = new List<HttpResponseMessage>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            responses.Add(await client.PostAsync(
                "/login",
                new FormUrlEncodedContent([
                    new("Input.Username", "unknown-user"),
                    new("Input.Password", "wrong-password"),
                    new("__RequestVerificationToken", token),
                ])));
        }

        Assert.All(responses.Take(5), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[5].StatusCode);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private sealed class ProductionSecurityFactory(string environment = "Development")
        : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");

        public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<WebPassDbContext>>();
                services.RemoveAll<WebPassDbContext>();
                services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
                services.AddDbContext<WebPassDbContext>(
                    options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
