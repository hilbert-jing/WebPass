using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Pages.Admin;
using Xunit;

namespace WebPass.IntegrationTests.Authorization;

public sealed class AdminUsersTests
{
    [Fact]
    public async Task Permission_replacement_records_before_and_after_codes_without_password_data()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        var operatorUser = NewUser("operator");
        db.Users.AddRange(administrator, operatorUser);
        db.UserPermissions.Add(new UserPermission { UserId = operatorUser.Id, PermissionCode = PermissionCode.AssetView });
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostReplacePermissionsAsync(operatorUser.Id, [PermissionCode.AssetCreate, PermissionCode.SubnetManage], default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal([PermissionCode.AssetCreate, PermissionCode.SubnetManage], await db.UserPermissions
            .Where(x => x.UserId == operatorUser.Id).Select(x => x.PermissionCode).Order().ToArrayAsync());
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("UserPermissionsReplace", audit.Action);
        Assert.Contains("beforePermissions", audit.Details);
        Assert.Contains("afterPermissions", audit.Details);
        Assert.DoesNotContain("PasswordHash", audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opaque-password-hash", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Permission_replacement_rejects_codes_outside_the_ordinary_user_whitelist()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        var operatorUser = NewUser("operator");
        db.Users.AddRange(administrator, operatorUser);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostReplacePermissionsAsync(operatorUser.Id, ["Administrator"], default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.UserPermissions.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Administrator_cannot_be_disabled_or_have_grants_replaced()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var disable = await model.OnPostSetEnabledAsync(administrator.Id, false, Convert.ToBase64String(administrator.RowVersion), default);
        var grants = await model.OnPostReplacePermissionsAsync(administrator.Id, [PermissionCode.AssetView], default);

        Assert.IsType<BadRequestObjectResult>(disable);
        Assert.IsType<BadRequestObjectResult>(grants);
        Assert.True((await db.Users.FindAsync(administrator.Id))!.IsEnabled);
        Assert.Empty(await db.UserPermissions.ToListAsync());
    }

    [Fact]
    public async Task Disabling_an_ordinary_user_is_audited_without_password_data()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        var operatorUser = NewUser("operator");
        db.Users.AddRange(administrator, operatorUser);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostSetEnabledAsync(operatorUser.Id, false, Convert.ToBase64String(operatorUser.RowVersion), default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.False((await db.Users.FindAsync(operatorUser.Id))!.IsEnabled);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("UserEnablement", audit.Action);
        Assert.Contains("beforeEnabled", audit.Details);
        Assert.Contains("afterEnabled", audit.Details);
        Assert.DoesNotContain("PasswordHash", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    private static UsersModel NewModel(WebPassDbContext db, Guid administratorId)
    {
        var model = new UsersModel(db, new PermissionAuthorizationHandler(db), new AuditWriter(db));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext() };
        model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, administratorId.ToString())], "test"));
        return model;
    }

    private static WebPassDbContext NewDatabase() => new(new DbContextOptionsBuilder<WebPassDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AppUser NewUser(string username, bool isAdministrator = false) => new()
    {
        Username = username,
        PasswordHash = "opaque-password-hash",
        IsAdministrator = isAdministrator,
    };
}
