using Microsoft.EntityFrameworkCore;
using WebPass.AdminInit;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.AdminInit;

public sealed class AdministratorInitializerTests
{
    [Fact]
    public async Task Creates_enabled_administrator_with_verifiable_password()
    {
        await using var db = CreateDatabase();
        var hasher = new Argon2PasswordHasher();
        var initializer = new AdministratorInitializer(db, hasher);

        var result = await initializer.CreateAsync(
            "  deploy-admin  ",
            "local-admin-password",
            "local-admin-password",
            default);

        Assert.Equal(AdministratorInitializationResultKind.Created, result.Kind);
        Assert.Equal("deploy-admin", result.Username);
        var user = await db.Users.SingleAsync();
        Assert.Equal("deploy-admin", user.Username);
        Assert.True(user.IsAdministrator);
        Assert.True(user.IsEnabled);
        Assert.False(user.MustChangePassword);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.True(hasher.Verify("local-admin-password", user.PasswordHash));
        Assert.Empty(user.Permissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creates_when_existing_user_is_ordinary_or_administrator(
        bool existingIsAdministrator)
    {
        await using var db = CreateDatabase();
        db.Users.Add(new AppUser
        {
            Username = "existing",
            PasswordHash = "existing-hash",
            IsAdministrator = existingIsAdministrator,
        });
        await db.SaveChangesAsync();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            "new-admin",
            "new-password",
            "new-password",
            default);

        Assert.Equal(AdministratorInitializationResultKind.Created, result.Kind);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.True((await db.Users.SingleAsync(x => x.Username == "new-admin"))
            .IsAdministrator);
    }

    [Fact]
    public async Task Duplicate_username_does_not_create_another_user()
    {
        await using var db = CreateDatabase();
        db.Users.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = "existing-hash",
            IsAdministrator = true,
        });
        await db.SaveChangesAsync();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            " admin ",
            "new-password",
            "new-password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.DuplicateUsername,
            result.Kind);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_username_does_not_write(string username)
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            username,
            "password",
            "password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.InvalidUsername,
            result.Kind);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Overlength_username_does_not_write()
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            new string('a', 129),
            "password",
            "password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.InvalidUsername,
            result.Kind);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("", "", AdministratorInitializationResultKind.InvalidPassword)]
    [InlineData("   ", "   ", AdministratorInitializationResultKind.InvalidPassword)]
    [InlineData("one", "two", AdministratorInitializationResultKind.PasswordMismatch)]
    public async Task Invalid_password_input_does_not_write(
        string password,
        string confirmation,
        AdministratorInitializationResultKind expected)
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            "admin",
            password,
            confirmation,
            default);

        Assert.Equal(expected, result.Kind);
        Assert.Empty(db.Users);
    }

    private static WebPassDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new WebPassDbContext(options);
    }
}
