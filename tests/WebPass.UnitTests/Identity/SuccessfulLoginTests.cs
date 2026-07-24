using System.Net;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.Identity;

public sealed class SuccessfulLoginTests
{
    [Fact]
    public async Task Correct_password_signs_in_and_resets_failed_login_count()
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
            FailedLoginCount = 3,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new LoginService(db, hasher);

        var result = await service.LoginAsync("operator", "correct-password", IPAddress.Loopback, default);

        Assert.Equal(LoginResultKind.Success, result.Kind);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
    }
}
