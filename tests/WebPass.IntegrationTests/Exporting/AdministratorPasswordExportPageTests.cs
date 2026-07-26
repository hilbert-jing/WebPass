using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
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
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.IntegrationTests.Exporting;

public sealed class AdministratorPasswordExportPageTests
{
    [Fact]
    public async Task Non_administrator_cannot_open_password_export_page()
    {
        using var factory = new PasswordExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(
            factory.OrdinaryUserId);

        using var response = await client.GetAsync(
            "/admin/password-export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Password_export_post_requires_antiforgery()
    {
        using var factory = new PasswordExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(
            factory.AdministratorId);

        using var response = await client.PostAsync(
            "/admin/password-export?handler=Download",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_without_grant_sees_warning_and_is_redirected_to_reauthentication()
    {
        using var factory = new PasswordExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(
            factory.AdministratorId);
        var html = await client.GetStringAsync("/admin/password-export");
        var token = AntiforgeryToken(html);

        Assert.Contains(
            "plaintext server passwords",
            html,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "name=\"Format\"",
            html,
            StringComparison.Ordinal);

        using var response = await client.PostAsync(
            "/admin/password-export?handler=Download",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
            ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            "/secrets/reauthenticate",
            response.Headers.Location!.OriginalString,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ReturnUrl=",
            response.Headers.Location.OriginalString,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reauthenticated_administrator_downloads_only_no_store_xlsx()
    {
        using var factory = new PasswordExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient(
            factory.AdministratorId);
        var html = await client.GetStringAsync("/admin/password-export");
        var token = AntiforgeryToken(html);
        using var reauthenticated = await client.PostAsync(
            "/secrets/reauthenticate",
            new FormUrlEncodedContent(
            [
                new("Input.Password", "current-password"),
                new("__RequestVerificationToken", token),
                new("ReturnUrl", "/admin/password-export"),
            ]));
        Assert.Equal(HttpStatusCode.Redirect, reauthenticated.StatusCode);

        using var response = await client.PostAsync(
            "/admin/password-export?handler=Download",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
            ]));
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(
            ".xlsx",
            response.Content.Headers.ContentDisposition!.FileNameStar,
            StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var workbook = new XLWorkbook(new MemoryStream(content));
        Assert.Equal(
            "server-password",
            workbook.Worksheet(1).Cell(2, 9).GetString());
    }

    private static string AntiforgeryToken(string html)
    {
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private sealed class PasswordExportPageFactory
        : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");

        public Guid AdministratorId { get; } = Guid.NewGuid();
        public Guid OrdinaryUserId { get; } = Guid.NewGuid();

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var administrator = new AppUser
            {
                Id = AdministratorId,
                Username = "administrator",
                PasswordHash = hasher.Hash("current-password"),
                IsAdministrator = true,
                RowVersion = [1, 2, 3],
            };
            var ordinaryUser = new AppUser
            {
                Id = OrdinaryUserId,
                Username = "operator",
                PasswordHash = "unused",
            };
            db.AddRange(administrator, ordinaryUser);
            db.UserPermissions.Add(new UserPermission
            {
                UserId = OrdinaryUserId,
                PermissionCode = PermissionCode.AssetView,
            });
            var subnet = new Subnet
            {
                Name = "Operations",
                Cidr = "10.0.0.0/24",
                NetworkAddress = "10.0.0.0",
                PrefixLength = 24,
                Location = "DC",
            };
            db.Subnets.Add(subnet);
            var asset = new ServerAsset
            {
                SubnetId = subnet.Id,
                BusinessIp = "10.0.0.10",
                BusinessIpNumber = 167772170,
                Location = "DC",
                AliveStatus = AliveStatus.Alive,
                ComputerName = "server-10",
                SystemName = "ERP",
                CreatedBy = AdministratorId,
            };
            db.ServerAssets.Add(asset);
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = asset.Id,
                Ciphertext = [1],
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
                UpdatedBy = AdministratorId,
            });
            db.SaveChanges();
        }

        public HttpClient CreateAuthenticatedClient(Guid userId)
        {
            using var scope = Services.CreateScope();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
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
            throw new NotSupportedException();

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            Task.FromResult("server-password");
    }
}
