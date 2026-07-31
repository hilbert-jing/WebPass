using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
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
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Exporting;

public sealed class ExportPageTests
{
    private const string UniqueServerPassword =
        "ordinary-export-secret-8f62f8a3";

    [Fact]
    public async Task Export_page_renders_chinese_secret_free_scope()
    {
        using var factory = new ExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = WebUtility.HtmlDecode(
            await client.GetStringAsync("/exports"));

        Assert.Contains("导出服务器数据", html, StringComparison.Ordinal);
        Assert.Contains("普通导出不包含服务器密码", html, StringComparison.Ordinal);
        Assert.Contains("class=\"export-scope", html, StringComparison.Ordinal);
        Assert.Contains("data-export-format", html, StringComparison.Ordinal);
        Assert.Contains("data-export-submit", html, StringComparison.Ordinal);
        Assert.Contains("存活", html, StringComparison.Ordinal);
        Assert.Contains("异常", html, StringComparison.Ordinal);
        Assert.Contains("停用", html, StringComparison.Ordinal);
        Assert.Contains("下载导出文件", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_requires_antiforgery()
    {
        using var factory = new ExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync(
            "/exports?handler=Download",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(
        "Csv",
        "text/csv",
        ".csv")]
    [InlineData(
        "Xlsx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xlsx")]
    public async Task Authorized_download_is_a_secret_free_no_store_attachment(
        string format,
        string contentType,
        string extension)
    {
        using var factory = new ExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/exports");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("Format", format),
            new("Query.Search", "server-10"),
        ]);

        using var response = await client.PostAsync(
            "/exports?handler=Download",
            form);
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            contentType,
            response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "attachment",
            response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(
            extension,
            response.Content.Headers.ContentDisposition.FileNameStar,
            StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.NoStore);
        if (format == "Csv")
        {
            var csv = Encoding.UTF8.GetString(content);
            Assert.Contains("10.0.0.10", csv, StringComparison.Ordinal);
            Assert.DoesNotContain(
                UniqueServerPassword,
                csv,
                StringComparison.Ordinal);
        }
        else
        {
            using var workbook = new XLWorkbook(new MemoryStream(content));
            Assert.Contains(
                workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()),
                cell => cell.GetString() == "10.0.0.10");
            Assert.DoesNotContain(
                workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()),
                cell => cell.GetString().Contains(
                    UniqueServerPassword,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Invalid_export_parameters_show_generic_chinese_error()
    {
        using var factory = new ExportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/exports");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;

        using var response = await client.PostAsync(
            "/exports?handler=Download",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("Format", "invalid-format"),
            ]));
        var responseHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "无法导出：请检查筛选条件和文件格式。",
            responseHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "invalid-format",
            responseHtml,
            StringComparison.Ordinal);
    }

    private sealed class ExportPageFactory : WebApplicationFactory<Program>
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
                Username = "exporter",
                PasswordHash = "unused",
            });
            db.UserPermissions.Add(new UserPermission
            {
                UserId = _userId,
                PermissionCode = PermissionCode.ExportData,
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
                CreatedBy = _userId,
            };
            db.ServerAssets.Add(asset);
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = asset.Id,
                Ciphertext = [1],
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
                UpdatedBy = _userId,
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
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        _userId.ToString()),
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
            Task.FromResult(UniqueServerPassword);
    }
}
