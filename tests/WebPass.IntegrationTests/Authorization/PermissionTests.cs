using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.IntegrationTests.Authorization;

public sealed class PermissionTests
{
    [Fact]
    public async Task Administrator_bypasses_individual_permission_rows()
    {
        await using var db = NewDatabase();
        var administrator = NewUser(isAdministrator: true);
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var handler = new PermissionAuthorizationHandler(db);

        Assert.True(await handler.IsAllowedAsync(administrator.Id, PermissionCode.SubnetManage, default));
    }

    [Fact]
    public async Task Ordinary_user_is_allowed_only_for_assigned_permission()
    {
        await using var db = NewDatabase();
        var user = NewUser();
        db.Users.Add(user);
        db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionCode = PermissionCode.SubnetManage });
        await db.SaveChangesAsync();
        var handler = new PermissionAuthorizationHandler(db);

        Assert.True(await handler.IsAllowedAsync(user.Id, PermissionCode.SubnetManage, default));
        Assert.False(await handler.IsAllowedAsync(user.Id, PermissionCode.AssetCreate, default));
    }

    [Fact]
    public async Task Disabled_user_is_denied_even_with_assigned_permission()
    {
        await using var db = NewDatabase();
        var user = NewUser(isEnabled: false);
        db.Users.Add(user);
        db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionCode = PermissionCode.SubnetManage });
        await db.SaveChangesAsync();
        var handler = new PermissionAuthorizationHandler(db);

        Assert.False(await handler.IsAllowedAsync(user.Id, PermissionCode.SubnetManage, default));
    }

    [Fact]
    public async Task Direct_backend_authorization_attempt_without_permission_does_not_succeed()
    {
        await using var db = NewDatabase();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var handler = new PermissionAuthorizationHandler(db);
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(PermissionCode.SubnetManage)],
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())], "test")),
            resource: null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static WebPassDbContext NewDatabase() => new(
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUser NewUser(bool isAdministrator = false, bool isEnabled = true) => new()
    {
        Username = Guid.NewGuid().ToString("N"),
        PasswordHash = "hash",
        IsAdministrator = isAdministrator,
        IsEnabled = isEnabled,
    };
}
