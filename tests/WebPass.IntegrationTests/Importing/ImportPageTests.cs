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
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Importing;

public sealed class ImportPageTests
{
    [Fact]
    public async Task Import_page_renders_chinese_upload_workspace()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/imports");

        Assert.Contains("导入服务器数据", html, StringComparison.Ordinal);
        Assert.Contains("最大 10 MB，最多 5,000 行", html, StringComparison.Ordinal);
        Assert.Contains("class=\"upload-zone\"", html, StringComparison.Ordinal);
        Assert.Contains("data-upload-zone", html, StringComparison.Ordinal);
        Assert.Contains("data-upload-input", html, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv,.xlsx\"", html, StringComparison.Ordinal);
    }

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
        Assert.Contains("新增", responseHtml, StringComparison.Ordinal);
        Assert.Contains(">1<", responseHtml, StringComparison.Ordinal);
        Assert.Contains("错误", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("server-password", responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocking_preview_shows_safe_errors_without_commit_or_raw_password()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/imports");
        var token = AntiforgeryToken(html);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        const string rawPassword = "never-render-this-password";
        const string csv =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n" +
            $"not-an-ip,DC,Unknown,server-10,ERP,,,,{rawPassword}\r\n";
        form.Add(new StringContent(csv), "Upload", "servers.csv");

        using var response = await client.PostAsync(
            "/imports?handler=Preview",
            form);
        var responseHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"import-errors", responseHtml, StringComparison.Ordinal);
        Assert.Contains("<th>行号</th>", responseHtml, StringComparison.Ordinal);
        Assert.Contains("<th>字段</th>", responseHtml, StringComparison.Ordinal);
        Assert.Contains("<th>原因</th>", responseHtml, StringComparison.Ordinal);
        Assert.Contains("必须修复文件后重新上传", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("提交导入", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPassword, responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_upload_uses_chinese_validation_message()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/imports");
        var token = AntiforgeryToken(html);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");

        using var response = await client.PostAsync(
            "/imports?handler=Preview",
            form);
        var responseHtml = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync());

        Assert.Contains(
            "请选择 CSV 或 XLSX 文件。",
            responseHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_result_is_shown_in_chinese_after_redirect()
    {
        using var factory = new ImportPageFactory();
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/imports");
        using var previewForm = new MultipartFormDataContent();
        previewForm.Add(
            new StringContent(AntiforgeryToken(html)),
            "__RequestVerificationToken");
        const string csv =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n" +
            "10.0.0.10,DC,Unknown,server-10,ERP,,,,server-password\r\n";
        previewForm.Add(new StringContent(csv), "Upload", "servers.csv");
        using var previewResponse = await client.PostAsync(
            "/imports?handler=Preview",
            previewForm);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync();
        var previewId = Regex.Match(
            previewHtml,
            "name=\"previewId\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;

        using var commitResponse = await client.PostAsync(
            "/imports?handler=Commit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", AntiforgeryToken(previewHtml)),
                new("previewId", previewId),
            ]));
        var resultHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync("/imports"));

        Assert.Equal(HttpStatusCode.Redirect, commitResponse.StatusCode);
        Assert.Contains(
            "已新增 1 项，更新 0 项，跳过 0 项",
            resultHtml,
            StringComparison.Ordinal);
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
            Task.FromResult(new SecretEnvelope([1], new byte[12], new byte[16], 1));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
