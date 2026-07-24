using System.Net;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.Identity;

public sealed class LoginServiceTests
{
    [Fact]
    public async Task Five_failed_logins_lock_the_user()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new WebPassDbContext(options);
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("correct-password"),
            FailedLoginCount = 4,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new LoginService(db, hasher);

        var result = await service.LoginAsync("operator", "wrong-password", IPAddress.Loopback, default);

        Assert.Equal(LoginResultKind.Locked, result.Kind);
        Assert.Equal(5, user.FailedLoginCount);
        Assert.NotNull(user.LockedUntil);
    }
}
