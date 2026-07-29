using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.AdminInit;

public enum AdministratorInitializationResultKind
{
    Created,
    InvalidUsername,
    InvalidPassword,
    PasswordMismatch,
    DuplicateUsername,
}

public sealed record AdministratorInitializationResult(
    AdministratorInitializationResultKind Kind,
    string? Username = null);

public sealed class AdministratorInitializer(
    WebPassDbContext db,
    IPasswordHasher passwordHasher)
{
    public async Task<AdministratorInitializationResult> CreateAsync(
        string? username,
        string? password,
        string? passwordConfirmation,
        CancellationToken ct)
    {
        var normalizedUsername = username?.Trim();
        if (string.IsNullOrEmpty(normalizedUsername)
            || normalizedUsername.Length > 128)
        {
            return new(AdministratorInitializationResultKind.InvalidUsername);
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return new(AdministratorInitializationResultKind.InvalidPassword);
        }

        if (!StringComparer.Ordinal.Equals(password, passwordConfirmation))
        {
            return new(AdministratorInitializationResultKind.PasswordMismatch);
        }

        if (await db.Users.AnyAsync(
            user => user.Username == normalizedUsername,
            ct))
        {
            return new(
                AdministratorInitializationResultKind.DuplicateUsername);
        }

        var user = new AppUser
        {
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            IsAdministrator = true,
            IsEnabled = true,
            MustChangePassword = false,
            FailedLoginCount = 0,
            LockedUntil = null,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException
            {
                Number: 2601 or 2627,
            })
        {
            db.Entry(user).State = EntityState.Detached;
            return new(
                AdministratorInitializationResultKind.DuplicateUsername);
        }

        return new(
            AdministratorInitializationResultKind.Created,
            normalizedUsername);
    }
}
