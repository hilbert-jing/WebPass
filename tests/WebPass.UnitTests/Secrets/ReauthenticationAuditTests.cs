using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class ReauthenticationAuditTests
{
    [Theory]
    [InlineData("current-password", "Success")]
    [InlineData("wrong-password", "Failure")]
    public async Task Verification_writes_a_redacted_audit_result(
        string submittedPassword,
        string expectedResult)
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new WebPassDbContext(options);
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            RowVersion = [1],
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ReauthenticationService(
            db,
            hasher,
            new InMemoryReauthenticationGrantStore(cache),
            new StubAuthenticationSessionFingerprint(),
            auditWriter: new AuditWriter(db));

        try
        {
            await service.VerifyAsync(user.Id, submittedPassword, default);
        }
        catch (UnauthorizedAccessException) when (expectedResult == "Failure")
        {
        }

        var audit = Assert.Single(await db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Equal("SecretReauthentication", audit.Action);
        Assert.Equal(expectedResult, audit.Result);
        Assert.Null(audit.Details);
    }

    private sealed class StubAuthenticationSessionFingerprint
        : IAuthenticationSessionFingerprint
    {
        public string GetCurrent() => "sha256:session-a";
    }
}
