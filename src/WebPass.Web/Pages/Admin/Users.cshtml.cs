using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.Web.Pages.Admin;

[Authorize(Policy = PermissionCode.AdministratorPolicy)]
public sealed class UsersModel(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter,
    IPasswordHasher passwordHasher) : PageModel
{
    private const string DefaultPassword = "abc123";

    public IReadOnlyList<AppUser> Users { get; private set; } = [];
    public IReadOnlySet<string> GrantablePermissions => PermissionCode.OrdinaryUserCodes;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(
        string username,
        CancellationToken ct)
    {
        await EnsureAdministratorAsync(ct);
        var normalizedUsername = username?.Trim() ?? string.Empty;
        if (normalizedUsername.Length is < 1 or > 128)
        {
            ModelState.AddModelError(
                "username",
                "Username must contain 1 to 128 characters.");
            await LoadAsync(ct);
            return Page();
        }

        if (await db.Users.AnyAsync(
                x => x.Username == normalizedUsername,
                ct))
        {
            ModelState.AddModelError(
                "username",
                "Username already exists.");
            await LoadAsync(ct);
            return Page();
        }

        var user = new AppUser
        {
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(DefaultPassword),
            IsAdministrator = false,
            IsEnabled = true,
            MustChangePassword = false,
            FailedLoginCount = 0,
            LockedUntil = null,
        };

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
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
            ModelState.AddModelError(
                "username",
                "Username already exists.");
            await LoadAsync(ct);
            return Page();
        }

        await auditWriter.WriteAsync(
            new AuditEntry(
                UserId(),
                "UserCreate",
                "User",
                user.Id.ToString(),
                "Success",
                null,
                Payload: new Dictionary<string, object?>
                {
                    ["username"] = normalizedUsername,
                }),
            ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        TempData["StatusMessage"] = $"已创建用户 {user.Username}。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(
        Guid userId,
        string rowVersion,
        CancellationToken ct)
    {
        await EnsureAdministratorAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(
                x => x.Id == userId,
                ct)
            ?? throw new KeyNotFoundException("User not found.");
        if (user.IsAdministrator)
        {
            return BadRequest(
                "Administrator passwords cannot be reset here.");
        }

        SetOriginalRowVersion(user, rowVersion);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        user.PasswordHash = passwordHasher.Hash(DefaultPassword);
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ObjectResult(
                "The user was changed by another administrator. Reload and try again.")
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
        }

        await auditWriter.WriteAsync(
            new AuditEntry(
                UserId(),
                "UserPasswordReset",
                "User",
                user.Id.ToString(),
                "Success",
                null),
            ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        TempData["StatusMessage"] =
            $"用户 {user.Username} 的密码已重置为系统预设初始密码。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetEnabledAsync(Guid userId, bool isEnabled, string rowVersion, CancellationToken ct)
    {
        await EnsureAdministratorAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (user.IsAdministrator)
        {
            return BadRequest("Administrator accounts cannot be downgraded or disabled here.");
        }

        SetOriginalRowVersion(user, rowVersion);
        var before = user.IsEnabled;
        user.IsEnabled = isEnabled;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ObjectResult("The user was changed by another administrator. Reload and try again.") { StatusCode = StatusCodes.Status409Conflict };
        }

        await auditWriter.WriteAsync(new AuditEntry(UserId(), "UserEnablement", "User", user.Id.ToString(), "Success", null,
            Payload: new Dictionary<string, object?> { ["beforeEnabled"] = before, ["afterEnabled"] = isEnabled }), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        TempData["StatusMessage"] =
            $"已{(isEnabled ? "启用" : "禁用")}用户 {user.Username}。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReplacePermissionsAsync(Guid userId, string[] selectedPermissions, CancellationToken ct)
    {
        await EnsureAdministratorAsync(ct);
        var user = await db.Users.Include(x => x.Permissions).SingleOrDefaultAsync(x => x.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        if (user.IsAdministrator)
        {
            return BadRequest("Administrator permission grants cannot be edited.");
        }

        var requested = selectedPermissions.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Any(code => !PermissionCode.OrdinaryUserCodes.Contains(code)))
        {
            return BadRequest("One or more permissions are not grantable.");
        }

        var before = user.Permissions.Select(x => x.PermissionCode).Order().ToArray();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        db.UserPermissions.RemoveRange(user.Permissions);
        db.UserPermissions.AddRange(requested.Select(code => new UserPermission { UserId = user.Id, PermissionCode = code }));
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(new AuditEntry(UserId(), "UserPermissionsReplace", "User", user.Id.ToString(), "Success", null,
            Payload: new Dictionary<string, object?> { ["beforePermissions"] = before, ["afterPermissions"] = requested.Order().ToArray() }), ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        TempData["StatusMessage"] =
            $"已更新用户 {user.Username} 的权限。";
        return RedirectToPage();
    }

    private async Task EnsureAdministratorAsync(CancellationToken ct)
    {
        if (!await permissions.IsAdministratorAsync(UserId(), ct))
        {
            throw new UnauthorizedAccessException("Administrator access is required.");
        }
    }

    private async Task LoadAsync(CancellationToken ct) => Users = await db.Users.Include(x => x.Permissions).AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private void SetOriginalRowVersion(AppUser user, string encodedRowVersion) => db.Entry(user).Property(x => x.RowVersion).OriginalValue = Convert.FromBase64String(encodedRowVersion);
}
