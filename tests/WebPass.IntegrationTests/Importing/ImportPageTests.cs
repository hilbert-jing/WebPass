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
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using Xunit;

namespace WebPass.IntegrationTests.Importing;

public sealed class ImportPageTests
{
    [Fact]
    public async Task Import_page_requires_antiforgery_before_processing_upload()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/imports?handler=Preview",
            new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Csv_upload_renders_encrypted_preview_summary()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/imports");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        const string csv =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n" +
            "10.0.0.10,DC,Unknown,server-10,ERP,,,,server-password\r\n";
        form.Add(new StringContent(csv), "Upload", "servers.csv");

        var response = await client.PostAsync("/imports?handler=Preview", form);
        var responseHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Create: 1", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("server-password", responseHtml, StringComparison.Ordinal);
    }

    private sealed class ImportPageFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        private readonly Guid _userId = Guid.NewGuid();

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Users.Add(new AppUser
            {
                Id = _userId,
                Username = "importer",
                PasswordHash = "unused",
            });
            db.UserPermissions.Add(new UserPermission
            {
                UserId = _userId,
                PermissionCode = PermissionCode.ImportData,
            });
            db.Subnets.Add(new Subnet
            {
                Name = "Operations",
                Cidr = "10.0.0.0/24",
                NetworkAddress = "10.0.0.0",
                PrefixLength = 24,
                Location = "DC",
            });
            db.SaveChanges();
        }

        public HttpClient CreateAuthenticatedClient()
        {
            using var scope = Services.CreateScope();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())],
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
                $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
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
            Task.FromResult(new SecretEnvelope([1], new byte[12], new byte[16], 1));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
