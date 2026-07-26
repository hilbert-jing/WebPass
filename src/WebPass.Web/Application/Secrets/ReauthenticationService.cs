using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.Web.Application.Secrets;

public sealed class ReauthenticationService(
    WebPassDbContext db,
    IPasswordHasher passwordHasher,
    IReauthenticationGrantStore grants,
    IAuthenticationSessionFingerprint sessionFingerprint,
    TimeProvider? clock = null,
    AuditWriter? auditWriter = null)
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(5);

    public async Task<ReauthenticationGrant> VerifyAsync(
        Guid userId,
        string password,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (user is null
            || !user.IsEnabled
            || user.MustChangePassword
            || user.LockedUntil > now
            || !passwordHasher.Verify(password, user.PasswordHash))
        {
            await WriteAuditAsync(userId, "Failure", ct);
            throw new UnauthorizedAccessException("Current-password verification failed.");
        }

        var grant = new ReauthenticationGrant(
            user.Id,
            sessionFingerprint.GetCurrent(),
            user.RowVersion.ToArray(),
            now.Add(GrantLifetime));
        await grants.StoreAsync(grant, ct);
        await WriteAuditAsync(userId, "Success", ct);
        return grant;
    }

    private Task WriteAuditAsync(Guid userId, string result, CancellationToken ct)
    {
        if (auditWriter is null)
        {
            return Task.CompletedTask;
        }

        return auditWriter.WriteAsync(
            new AuditEntry(userId, "SecretReauthentication", "User", userId.ToString(), result, null),
            ct);
    }
}
