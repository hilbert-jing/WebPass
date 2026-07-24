using System.Net;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;

namespace WebPass.Web.Infrastructure.Identity;

public enum LoginResultKind
{
    Success,
    InvalidCredentials,
    Locked,
    Disabled,
}

public sealed record LoginResult(LoginResultKind Kind, Guid? UserId = null);

public sealed class LoginService(
    WebPassDbContext db,
    IPasswordHasher passwordHasher,
    AuditWriter? auditWriter = null,
    TimeProvider? clock = null)
{
    private const int FailedLoginLimit = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public async Task<LoginResult> LoginAsync(string username, string password, IPAddress sourceIp, CancellationToken ct)
    {
        var normalizedUsername = username.Trim();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == normalizedUsername, ct);
        if (user is null)
        {
            return await LoginResult(LoginResultKind.InvalidCredentials, null, normalizedUsername, sourceIp, ct);
        }

        if (!user.IsEnabled)
        {
            return await RecordAsync(LoginResultKind.Disabled, user, sourceIp, ct);
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return await RecordAsync(LoginResultKind.Locked, user, sourceIp, ct);
        }

        if (!passwordHasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            var resultKind = LoginResultKind.InvalidCredentials;
            if (user.FailedLoginCount >= FailedLoginLimit)
            {
                user.LockedUntil = now.Add(LockDuration);
                resultKind = LoginResultKind.Locked;
            }

            await db.SaveChangesAsync(ct);
            return await RecordAsync(resultKind, user, sourceIp, ct);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return await RecordAsync(LoginResultKind.Success, user, sourceIp, ct);
    }

    private async Task<LoginResult> RecordAsync(LoginResultKind kind, WebPass.Web.Domain.Entities.AppUser user, IPAddress sourceIp, CancellationToken ct)
    {
        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(new AuditEntry(user.Id, "Login", "User", user.Id.ToString(), kind.ToString(), sourceIp), ct);
        }

        return new LoginResult(kind, kind == LoginResultKind.Success ? user.Id : null);
    }

    private async Task<LoginResult> LoginResult(LoginResultKind kind, Guid? userId, string username, IPAddress sourceIp, CancellationToken ct)
    {
        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(new AuditEntry(userId, "Login", "User", username, kind.ToString(), sourceIp), ct);
        }

        return new LoginResult(kind, userId);
    }
}
