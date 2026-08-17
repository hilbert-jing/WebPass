using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.Identity;

public sealed class PasswordChangeServiceTests
{
    [Fact]
    public async Task Enabled_user_changes_own_password_and_writes_redacted_audit()
    {
        await using var db = NewDatabase();
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            MustChangePassword = true,
            FailedLoginCount = 3,
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new PasswordChangeService(
            db,
            hasher,
            new AuditWriter(db));

        var result = await service.ChangeAsync(
            user.Id,
            "current-password",
            "new-password",
            default);

        Assert.Equal(PasswordChangeResultKind.Success, result.Kind);
        Assert.False(hasher.Verify("current-password", user.PasswordHash));
        Assert.True(hasher.Verify("new-password", user.PasswordHash));
        Assert.False(user.MustChangePassword);
        Assert.Equal(3, user.FailedLoginCount);
        Assert.NotNull(user.LockedUntil);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("UserPasswordChange", audit.Action);
        Assert.Equal("User", audit.ObjectType);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Equal(user.Id.ToString(), audit.ObjectId);
        Assert.Equal("Success", audit.Result);
        Assert.Null(audit.Details);
    }

    [Theory]
    [InlineData(Rejection.Missing, PasswordChangeResultKind.UserUnavailable)]
    [InlineData(Rejection.Disabled, PasswordChangeResultKind.UserUnavailable)]
    [InlineData(Rejection.WrongCurrent, PasswordChangeResultKind.IncorrectCurrentPassword)]
    [InlineData(Rejection.WhitespaceNew, PasswordChangeResultKind.InvalidNewPassword)]
    public async Task Rejected_change_does_not_mutate_the_user_or_write_audit(
        Rejection rejection,
        PasswordChangeResultKind expected)
    {
        await using var db = NewDatabase();
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            IsEnabled = rejection != Rejection.Disabled,
            MustChangePassword = true,
        };
        var userId = user.Id;
        if (rejection != Rejection.Missing)
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var originalHash = user.PasswordHash;
        var service = new PasswordChangeService(
            db,
            hasher,
            new AuditWriter(db));

        var result = await service.ChangeAsync(
            userId,
            rejection == Rejection.WrongCurrent
                ? "wrong-password"
                : "current-password",
            rejection == Rejection.WhitespaceNew ? "   " : "new-password",
            default);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(originalHash, user.PasswordHash);
        Assert.True(user.MustChangePassword);
        Assert.Empty(db.AuditLogs);
    }

    private static WebPassDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    public enum Rejection
    {
        Missing,
        Disabled,
        WrongCurrent,
        WhitespaceNew,
    }
}
