using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Pages.Admin;
using Xunit;

namespace WebPass.IntegrationTests.Authorization;

public sealed class AdminUsersTests
{
    [Fact]
    public async Task Create_adds_an_enabled_ordinary_user_with_default_password_and_no_permissions()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostCreateAsync("  operator  ", default);

        Assert.IsType<RedirectToPageResult>(result);
        var created = await db.Users.SingleAsync(x => x.Username == "operator");
        Assert.False(created.IsAdministrator);
        Assert.True(created.IsEnabled);
        Assert.False(created.MustChangePassword);
        Assert.Equal(0, created.FailedLoginCount);
        Assert.Null(created.LockedUntil);
        Assert.Empty(await db.UserPermissions
            .Where(x => x.UserId == created.Id)
            .ToListAsync());
        Assert.True(new Argon2PasswordHasher()
            .Verify("abc123", created.PasswordHash));
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("UserCreate", audit.Action);
        Assert.DoesNotContain(
            "abc123",
            audit.Details ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            created.PasswordHash,
            audit.Details ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            "已创建用户。",
            model.TempData["StatusMessage"]);
        Assert.DoesNotContain(
            created.Username,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "abc123",
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            created.PasswordHash,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_username_without_writing(
        string username)
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostCreateAsync(username, default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(
            "请输入用户名。",
            Assert.Single(model.ModelState["username"]!.Errors).ErrorMessage);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Create_rejects_a_username_longer_than_128_characters()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostCreateAsync(
            new string('a', 129),
            default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(
            "用户名不能超过 128 个字符。",
            Assert.Single(model.ModelState["username"]!.Errors).ErrorMessage);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_normalized_username()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        var existing = NewUser("operator");
        db.Users.AddRange(administrator, existing);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostCreateAsync(" operator ", default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(
            "该用户名已存在，请使用其他用户名。",
            Assert.Single(model.ModelState["username"]!.Errors).ErrorMessage);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Create_is_not_blocked_by_existing_ordinary_users_or_administrators()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        db.Users.AddRange(
            administrator,
            NewUser("existing-ordinary"),
            NewUser("existing-administrator", isAdministrator: true));
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostCreateAsync(
            "new-ordinary",
            default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(4, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Reset_rehashes_default_password_clears_lock_and_preserves_account_shape()
    {
        await using var db = NewDatabase();
        var hasher = new Argon2PasswordHasher();
        var administrator = NewUser("administrator", isAdministrator: true);
        var user = NewUser("operator");
        user.PasswordHash = hasher.Hash("old-password");
        user.IsEnabled = false;
        user.MustChangePassword = true;
        user.FailedLoginCount = 5;
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        user.RowVersion = [1];
        db.Users.AddRange(administrator, user);
        db.UserPermissions.Add(new UserPermission
        {
            UserId = user.Id,
            PermissionCode = PermissionCode.AssetView,
        });
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostResetPasswordAsync(
            user.Id,
            Convert.ToBase64String([1]),
            default);

        Assert.IsType<RedirectToPageResult>(result);
        var reset = await db.Users
            .Include(x => x.Permissions)
            .SingleAsync(x => x.Id == user.Id);
        Assert.True(hasher.Verify("abc123", reset.PasswordHash));
        Assert.False(reset.IsEnabled);
        Assert.False(reset.MustChangePassword);
        Assert.Equal(0, reset.FailedLoginCount);
        Assert.Null(reset.LockedUntil);
        Assert.Equal(
            [PermissionCode.AssetView],
            reset.Permissions.Select(x => x.PermissionCode).ToArray());
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("UserPasswordReset", audit.Action);
        Assert.DoesNotContain(
            "abc123",
            audit.Details ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            reset.PasswordHash,
            audit.Details ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            "用户密码已重置为系统预设初始密码。",
            model.TempData["StatusMessage"]);
        Assert.DoesNotContain(
            reset.Username,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "abc123",
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            reset.PasswordHash,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_rejects_an_administrator_target()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        administrator.RowVersion = [1];
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostResetPasswordAsync(
            administrator.Id,
            Convert.ToBase64String([1]),
            default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Reset_returns_conflict_for_a_stale_row_version()
    {
        await using var db = NewDatabase();
        var administrator = NewUser("administrator", isAdministrator: true);
        var user = NewUser("operator");
        user.RowVersion = [1];
        db.Users.AddRange(administrator, user);
        await db.SaveChangesAsync();
        var originalHash = user.PasswordHash;
        var model = NewModel(db, administrator.Id);

        var result = await model.OnPostResetPasswordAsync(
            user.Id,
            Convert.ToBase64String([9]),
            default);

        Assert.Equal(
            StatusCodes.Status409Conflict,
            Assert.IsType<ObjectResult>(result).StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(
            originalHash,
            (await db.Users.SingleAsync(x => x.Id == user.Id)).PasswordHash);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Ordinary_user_cannot_create_or_reset_users()
    {
        await using var db = NewDatabase();
        var ordinary = NewUser("ordinary");
        var target = NewUser("target");
        target.RowVersion = [1];
        db.Users.AddRange(ordinary, target);
        await db.SaveChangesAsync();
        var model = NewModel(db, ordinary.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => model.OnPostCreateAsync("new-user", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => model.OnPostResetPasswordAsync(
                target.Id,
                Convert.ToBase64String([1]),
                default));
    }

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
        Assert.Equal(
            "已更新用户权限。",
            model.TempData["StatusMessage"]);
        Assert.DoesNotContain(
            operatorUser.Username,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
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
        Assert.Equal(
            "已禁用用户。",
            model.TempData["StatusMessage"]);
        Assert.DoesNotContain(
            operatorUser.Username,
            model.TempData["StatusMessage"]?.ToString(),
            StringComparison.Ordinal);
    }

    private static UsersModel NewModel(WebPassDbContext db, Guid administratorId)
    {
        var model = new UsersModel(
            db,
            new PermissionAuthorizationHandler(db),
            new AuditWriter(db),
            new Argon2PasswordHasher());
        var httpContext = new DefaultHttpContext();
        var tempData = new TempDataDictionary(
            httpContext,
            new InMemoryTempDataProvider());
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ITempDataDictionaryFactory>(
                new InMemoryTempDataDictionaryFactory(tempData))
            .BuildServiceProvider();
        model.PageContext = new PageContext
        {
            HttpContext = httpContext,
        };
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

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(
            HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }

    private sealed class InMemoryTempDataDictionaryFactory(
        ITempDataDictionary tempData) : ITempDataDictionaryFactory
    {
        public ITempDataDictionary GetTempData(
            HttpContext context) => tempData;
    }
}
