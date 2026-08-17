using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;

namespace WebPass.Web.Infrastructure.Identity;

public enum PasswordChangeResultKind
{
    Success,
    UserUnavailable,
    IncorrectCurrentPassword,
    InvalidNewPassword,
    ConcurrencyConflict,
}

public sealed record PasswordChangeResult(PasswordChangeResultKind Kind);

public sealed class PasswordChangeService(
    WebPassDbContext db,
    IPasswordHasher passwordHasher,
    AuditWriter auditWriter)
{
    public Task<bool> CanChangeAsync(
        Guid userId,
        CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(
            user => user.Id == userId && user.IsEnabled,
            ct);

    public async Task<PasswordChangeResult> ChangeAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId,
            ct);
        if (user is null || !user.IsEnabled)
        {
            return new(PasswordChangeResultKind.UserUnavailable);
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return new(PasswordChangeResultKind.InvalidNewPassword);
        }

        if (string.IsNullOrWhiteSpace(currentPassword)
            || !passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return new(PasswordChangeResultKind.IncorrectCurrentPassword);
        }

        var replacementHash = passwordHasher.Hash(newPassword);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        user.PasswordHash = replacementHash;
        user.MustChangePassword = false;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            return new(PasswordChangeResultKind.ConcurrencyConflict);
        }

        await auditWriter.WriteAsync(
            new AuditEntry(
                user.Id,
                "UserPasswordChange",
                "User",
                user.Id.ToString(),
                "Success",
                null),
            ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new(PasswordChangeResultKind.Success);
    }
}
