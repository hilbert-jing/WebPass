using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class ReauthenticationServiceTests
{
    [Fact]
    public async Task Correct_current_password_creates_five_minute_session_bound_grant_without_changing_login_state()
    {
        var now = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new WebPassDbContext(options);
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            FailedLoginCount = 3,
            LockedUntil = now.AddMinutes(-1),
            RowVersion = [1, 2, 3],
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var grants = new InMemoryReauthenticationGrantStore(cache, clock);
        var session = new StubAuthenticationSessionFingerprint("sha256:session-a");
        var service = new ReauthenticationService(db, hasher, grants, session, clock);

        var grant = await service.VerifyAsync(user.Id, "current-password", default);

        Assert.Equal(user.Id, grant.UserId);
        Assert.Equal(now.AddMinutes(5), grant.ExpiresAt);
        Assert.True(await grants.HasValidGrantAsync(
            user.Id, "sha256:session-a", user.RowVersion, default));
        Assert.Equal(3, user.FailedLoginCount);
        Assert.Equal(now.AddMinutes(-1), user.LockedUntil);
    }

    [Fact]
    public async Task Grant_is_rejected_after_five_minutes_for_another_session_or_changed_user()
    {
        var now = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var grants = new InMemoryReauthenticationGrantStore(cache, clock);
        var userId = Guid.NewGuid();
        await grants.StoreAsync(
            new ReauthenticationGrant(userId, "sha256:session-a", [1, 2, 3], now.AddMinutes(5)),
            default);

        Assert.False(await grants.HasValidGrantAsync(userId, "sha256:session-b", [1, 2, 3], default));
        Assert.False(await grants.HasValidGrantAsync(userId, "sha256:session-a", [1, 2, 4], default));

        clock.UtcNow = now.AddMinutes(5);

        Assert.False(await grants.HasValidGrantAsync(userId, "sha256:session-a", [1, 2, 3], default));
    }

    [Fact]
    public async Task Wrong_current_password_is_rejected_without_changing_login_state()
    {
        var now = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new WebPassDbContext(options);
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            FailedLoginCount = 4,
            RowVersion = [1],
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var grants = new InMemoryReauthenticationGrantStore(cache, new FixedTimeProvider(now));
        var service = new ReauthenticationService(
            db,
            hasher,
            grants,
            new StubAuthenticationSessionFingerprint("sha256:session-a"),
            new FixedTimeProvider(now));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.VerifyAsync(user.Id, "wrong-password", default));

        Assert.Equal(4, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.False(await grants.HasValidGrantAsync(user.Id, "sha256:session-a", user.RowVersion, default));
    }

    private sealed class StubAuthenticationSessionFingerprint(string value)
        : IAuthenticationSessionFingerprint
    {
        public string GetCurrent() => value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
