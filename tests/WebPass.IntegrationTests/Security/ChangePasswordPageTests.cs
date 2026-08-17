using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebPass.IntegrationTests.Presentation;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.IntegrationTests.Security;

public sealed class ChangePasswordPageTests
{
    [Fact]
    public async Task Enabled_user_changes_password_through_the_page()
    {
        using var factory = new PresentationFactory();
        SeedUser(factory, "current-password");
        using var client = factory.CreateAuthenticatedClient();
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            "/account/change-password",
            Form(
                token,
                "current-password",
                "new-password",
                "new-password"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/account/change-password",
            response.Headers.Location?.OriginalString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == factory.UserId);
        var hasher = new Argon2PasswordHasher();
        Assert.False(hasher.Verify("current-password", user.PasswordHash));
        Assert.True(hasher.Verify("new-password", user.PasswordHash));
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("UserPasswordChange", audit.Action);
        Assert.Null(audit.Details);
    }

    [Theory]
    [InlineData(
        "wrong-current-value",
        "new-password-value",
        "new-password-value",
        "当前密码不正确。")]
    [InlineData(
        "valid-current-value",
        "first-new-password",
        "different-confirmation",
        "两次输入的新密码不一致。")]
    public async Task Rejected_form_never_echoes_password_values(
        string currentPassword,
        string newPassword,
        string confirmation,
        string expectedError)
    {
        using var factory = new PresentationFactory();
        SeedUser(factory, "valid-current-value");
        using var client = factory.CreateAuthenticatedClient();
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            "/account/change-password",
            Form(token, currentPassword, newPassword, confirmation));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            expectedError,
            WebUtility.HtmlDecode(html),
            StringComparison.Ordinal);
        Assert.DoesNotContain(currentPassword, html, StringComparison.Ordinal);
        Assert.DoesNotContain(newPassword, html, StringComparison.Ordinal);
        Assert.DoesNotContain(confirmation, html, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == factory.UserId);
        Assert.True(new Argon2PasswordHasher().Verify(
            "valid-current-value",
            user.PasswordHash));
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Disabled_user_cannot_open_the_page()
    {
        using var factory = new PresentationFactory();
        SeedUser(factory, "current-password", isEnabled: false);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(
            "/account/change-password");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static void SeedUser(
        PresentationFactory factory,
        string password,
        bool isEnabled = true)
    {
        var hash = new Argon2PasswordHasher().Hash(password);
        factory.Seed(db => db.Users.Add(new AppUser
        {
            Id = factory.UserId,
            Username = "password-change-user",
            PasswordHash = hash,
            IsEnabled = isEnabled,
        }));
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/account/change-password");
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    private static FormUrlEncodedContent Form(
        string token,
        string currentPassword,
        string newPassword,
        string confirmation) =>
        new([
            new("Input.CurrentPassword", currentPassword),
            new("Input.NewPassword", newPassword),
            new("Input.NewPasswordConfirmation", confirmation),
            new("__RequestVerificationToken", token),
        ]);
}
