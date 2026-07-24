using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;

namespace WebPass.Web.Infrastructure.Authorization;

public sealed class PermissionAuthorizationHandler(WebPassDbContext db) : IAuthorizationHandler
{
    public async Task<bool> IsAllowedAsync(Guid userId, string permissionCode, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null || !user.IsEnabled) return false;
        return user.IsAdministrator || await db.UserPermissions.AsNoTracking().AnyAsync(
            x => x.UserId == userId && x.PermissionCode == permissionCode, ct);
    }

    public Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct) => db.Users.AsNoTracking().AnyAsync(
        x => x.Id == userId && x.IsEnabled && x.IsAdministrator, ct);

    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (!TryGetUserId(context.User, out var userId)) return;
        foreach (var requirement in context.Requirements.OfType<PermissionRequirement>())
        {
            if (await IsAllowedAsync(userId, requirement.Code, CancellationToken.None)) context.Succeed(requirement);
        }
        foreach (var requirement in context.Requirements.OfType<AdministratorRequirement>())
        {
            if (await IsAdministratorAsync(userId, CancellationToken.None)) context.Succeed(requirement);
        }
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId) => Guid.TryParse(
        user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
