# WebPass User Management, Session Limits, and Migration Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add administrator-driven ordinary-user creation and fixed-password reset, enforce a 30-minute idle and 8-hour absolute login lifetime with explicit logout, and provide a repeatable Windows migration-bundle build and deployment flow.

**Architecture:** Extend the existing administrator Razor Page directly for user mutations and use the existing `WebPassDbContext`, Argon2id hasher, authorization handler, audit writer, transactions, and row-version pattern. Configure Cookie Authentication directly in `Program.cs`, store the original login instant in the protected ticket, and add a small POST-only logout Razor Page. Generate the EF Core bundle through the repository-local tool from a PowerShell script; do not add schema changes or commit the generated executable.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages and Cookie Authentication, EF Core 10 with SQL Server, Argon2id, xUnit, `WebApplicationFactory`, PowerShell 5.1+, repository-local `dotnet-ef` 10.0.0.

## Global Constraints

- Work in `E:\MyJob\WebPass\.worktrees\webpass-core` on `feature/webpass-core`.
- Preserve the existing local authentication, authorization, Cookie security attributes, login lockout, Argon2id parameters, audit writer, and permission model.
- The fixed ordinary-user initial and reset password is exactly `abc123`.
- New and reset users have `MustChangePassword = false`; do not add forced or self-service password changes.
- A new user is enabled, is not an administrator, and has zero `UserPermission` rows.
- Password reset is available only for ordinary users and does not revoke their existing authentication Cookie.
- Never write `abc123`, password hashes, Cookie values, claims, or connection strings to audit payloads or user-visible exception output.
- Session idle lifetime is exactly 30 minutes; absolute lifetime is exactly 8 hours from the original successful login.
- The migration bundle is framework-dependent, targets `win-x64`, is named `WebPass.Migrations.exe`, and is never committed to Git.
- Do not modify entities, `WebPassDbContext`, model snapshots, or existing migrations; no new EF migration is expected.
- Do not introduce ASP.NET Core Identity, a session database, a security stamp, email, notification, or account-recovery workflows.
- Use strict TDD for behavior changes: observe RED before production edits, then GREEN.
- Stop after each task checkpoint, report the expected files and token cost for the next task, and obtain approval before continuing.

## File Map

- `src/WebPass.Web/Pages/Admin/Users.cshtml.cs`: create and reset handlers, validation, hashing, concurrency, transactions, and audit.
- `src/WebPass.Web/Pages/Admin/Users.cshtml`: username creation form and ordinary-user reset buttons.
- `tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs`: direct page-model behavior, authorization, hashing, auditing, and concurrency coverage.
- `src/WebPass.Web/Pages/Login.cshtml.cs`: original-login UTC Claim written into the protected authentication ticket.
- `src/WebPass.Web/Program.cs`: 30-minute sliding Cookie settings and inline 8-hour absolute-lifetime validation.
- `src/WebPass.Web/Pages/Logout.cshtml`: POST endpoint declaration.
- `src/WebPass.Web/Pages/Logout.cshtml.cs`: logout audit, Cookie sign-out, and redirect.
- `src/WebPass.Web/Pages/Shared/_Layout.cshtml`: authenticated-user POST logout form.
- `tests/WebPass.IntegrationTests/Security/AuthenticationSessionTests.cs`: Cookie options, ticket validation, login Claim, and logout behavior.
- `scripts/Build-WebPassMigrationBundle.ps1`: repeatable repository-local migration-bundle build.
- `docs/deployment/windows-server-iis.md`: bundle build, transfer, execution, and deployment ordering.
- `tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`: actual bundle generation and SQL Server migration execution.

---

### Task 1: Add ordinary-user creation and password reset

**Checkpoint estimate:** 3 modified files; 12k–18k tokens.

**Files:**
- Modify: `src/WebPass.Web/Pages/Admin/Users.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Admin/Users.cshtml`
- Modify: `tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs`

**Interfaces:**
- Consumes: `IPasswordHasher.Hash(string)`, `PermissionAuthorizationHandler.IsAdministratorAsync(Guid, CancellationToken)`, `AuditWriter.WriteAsync(AuditEntry, CancellationToken)`, `AppUser.RowVersion`.
- Produces:

```csharp
public Task<IActionResult> OnPostCreateAsync(
    string username,
    CancellationToken ct);

public Task<IActionResult> OnPostResetPasswordAsync(
    Guid userId,
    string rowVersion,
    CancellationToken ct);
```

- Fixed password constant remains private to `UsersModel`.
- Existing handlers and route `/admin/users` retain their current contracts.

- [ ] **Step 1: Extend the page-model tests before changing production code**

In `AdminUsersTests.cs`, update `NewModel` to pass a real
`Argon2PasswordHasher`, then add these tests:

```csharp
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
    Assert.Empty(await db.UserPermissions.Where(x => x.UserId == created.Id).ToListAsync());
    Assert.True(new Argon2PasswordHasher().Verify("abc123", created.PasswordHash));
    var audit = Assert.Single(db.AuditLogs);
    Assert.Equal("UserCreate", audit.Action);
    Assert.DoesNotContain("abc123", audit.Details ?? string.Empty, StringComparison.Ordinal);
    Assert.DoesNotContain(created.PasswordHash, audit.Details ?? string.Empty, StringComparison.Ordinal);
}

[Theory]
[InlineData("")]
[InlineData("   ")]
public async Task Create_rejects_an_empty_username_without_writing(string username)
{
    await using var db = NewDatabase();
    var administrator = NewUser("administrator", isAdministrator: true);
    db.Users.Add(administrator);
    await db.SaveChangesAsync();
    var model = NewModel(db, administrator.Id);

    var result = await model.OnPostCreateAsync(username, default);

    Assert.IsType<PageResult>(result);
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

    Assert.IsType<RedirectToPageResult>(
        await model.OnPostCreateAsync("new-ordinary", default));
    Assert.Equal(4, await db.Users.CountAsync());
}

[Fact]
public async Task Reset_rehashes_default_password_clears_lock_and_preserves_account_shape()
{
    await using var db = NewDatabase();
    var administrator = NewUser("administrator", isAdministrator: true);
    var user = NewUser("operator");
    user.PasswordHash = new Argon2PasswordHasher().Hash("old-password");
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
    var reset = await db.Users.Include(x => x.Permissions).SingleAsync(x => x.Id == user.Id);
    Assert.True(new Argon2PasswordHasher().Verify("abc123", reset.PasswordHash));
    Assert.False(reset.IsEnabled);
    Assert.False(reset.MustChangePassword);
    Assert.Equal(0, reset.FailedLoginCount);
    Assert.Null(reset.LockedUntil);
    Assert.Equal([PermissionCode.AssetView], reset.Permissions.Select(x => x.PermissionCode).ToArray());
    var audit = Assert.Single(db.AuditLogs);
    Assert.Equal("UserPasswordReset", audit.Action);
    Assert.DoesNotContain("abc123", audit.Details ?? string.Empty, StringComparison.Ordinal);
    Assert.DoesNotContain(reset.PasswordHash, audit.Details ?? string.Empty, StringComparison.Ordinal);
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

    Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
    db.ChangeTracker.Clear();
    Assert.Equal(originalHash, (await db.Users.SingleAsync(x => x.Id == user.Id)).PasswordHash);
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
```

Change the helper constructor call exactly to:

```csharp
private static UsersModel NewModel(WebPassDbContext db, Guid administratorId)
{
    var model = new UsersModel(
        db,
        new PermissionAuthorizationHandler(db),
        new AuditWriter(db),
        new Argon2PasswordHasher());
    model.PageContext = new PageContext
    {
        HttpContext = new DefaultHttpContext(),
    };
    model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, administratorId.ToString())],
        "test"));
    return model;
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~AdminUsersTests
```

Expected: compilation fails because `UsersModel` has no four-argument
constructor, `OnPostCreateAsync`, or `OnPostResetPasswordAsync`.

- [ ] **Step 3: Implement creation and reset directly in `UsersModel`**

Add:

```csharp
using Microsoft.Data.SqlClient;
using WebPass.Web.Infrastructure.Identity;
```

Change the primary constructor and add the constant:

```csharp
public sealed class UsersModel(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter,
    IPasswordHasher passwordHasher) : PageModel
{
    private const string DefaultPassword = "abc123";
```

Add the handlers:

```csharp
public async Task<IActionResult> OnPostCreateAsync(
    string username,
    CancellationToken ct)
{
    await EnsureAdministratorAsync(ct);
    var normalizedUsername = username?.Trim() ?? string.Empty;
    if (normalizedUsername.Length is < 1 or > 128)
    {
        ModelState.AddModelError("username", "Username must contain 1 to 128 characters.");
        await LoadAsync(ct);
        return Page();
    }

    if (await db.Users.AnyAsync(x => x.Username == normalizedUsername, ct))
    {
        ModelState.AddModelError("username", "Username already exists.");
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
        ModelState.AddModelError("username", "Username already exists.");
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

    return RedirectToPage();
}

public async Task<IActionResult> OnPostResetPasswordAsync(
    Guid userId,
    string rowVersion,
    CancellationToken ct)
{
    await EnsureAdministratorAsync(ct);
    var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct)
        ?? throw new KeyNotFoundException("User not found.");
    if (user.IsAdministrator)
    {
        return BadRequest("Administrator passwords cannot be reset here.");
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

    return RedirectToPage();
}
```

Do not query total user or administrator counts. Do not add
`UserPermission` rows in the create handler.

- [ ] **Step 4: Add the two forms to the existing page**

Immediately below the `<h1>` in `Users.cshtml`, add:

```html
<section aria-labelledby="create-user-heading">
    <h2 id="create-user-heading">Create ordinary user</h2>
    <form method="post" asp-page-handler="Create">
        <div asp-validation-summary="All"></div>
        <label for="username">Username</label>
        <input id="username" name="username" maxlength="128"
               autocomplete="off" required />
        <button type="submit">Create user</button>
    </form>
</section>
```

Inside the existing `if (!user.IsAdministrator)` action block, before the
enablement form, add:

```html
<form method="post" asp-page-handler="ResetPassword">
    <input type="hidden" name="userId" value="@user.Id" />
    <input type="hidden" name="rowVersion"
           value="@Convert.ToBase64String(user.RowVersion)" />
    <button type="submit">Reset password</button>
</form>
```

Do not render or transmit the default password in the HTML.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the command from Step 2.

Expected: all `AdminUsersTests` pass with zero failures and skips.

- [ ] **Step 6: Run authorization and identity regressions**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter "FullyQualifiedName~Authorization"
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj `
  -c Release --filter "FullyQualifiedName~Identity"
```

Expected: both commands pass; existing enablement, permission replacement,
login lockout, and Argon2id behavior remain unchanged.

- [ ] **Step 7: Review and commit Task 1**

Run:

```powershell
git diff --check
git diff -- src/WebPass.Web/Pages/Admin/Users.cshtml.cs `
  src/WebPass.Web/Pages/Admin/Users.cshtml `
  tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs
git status --short
```

Stage only the three Task 1 files and commit:

```powershell
git add -- src/WebPass.Web/Pages/Admin/Users.cshtml.cs `
  src/WebPass.Web/Pages/Admin/Users.cshtml `
  tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs
git commit -m "feat: add ordinary user creation and password reset"
```

Stop at the checkpoint and obtain approval for Task 2.

---

### Task 2: Enforce session limits and add explicit logout

**Checkpoint estimate:** 6 files, 14k–20k tokens.

**Files:**
- Modify: `src/WebPass.Web/Pages/Login.cshtml.cs`
- Modify: `src/WebPass.Web/Program.cs`
- Create: `src/WebPass.Web/Pages/Logout.cshtml`
- Create: `src/WebPass.Web/Pages/Logout.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Shared/_Layout.cshtml`
- Create: `tests/WebPass.IntegrationTests/Security/AuthenticationSessionTests.cs`

**Interfaces:**
- Produces:

```csharp
public const string LoginModel.SessionStartedClaimType =
    "webpass:session-started-utc";

public sealed class LogoutModel(AuditWriter auditWriter) : PageModel
{
    public IActionResult OnGet();
    public Task<IActionResult> OnPostAsync(CancellationToken ct);
}
```

- Cookie options remain on
  `CookieAuthenticationDefaults.AuthenticationScheme`.
- The original login instant is a Unix-seconds Claim inside the protected
  authentication ticket.

- [ ] **Step 1: Write the failing session and logout tests**

Create `AuthenticationSessionTests.cs` with these tests:

```csharp
using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Security;

public sealed class AuthenticationSessionTests
{
    [Fact]
    public void Cookie_uses_thirty_minute_sliding_expiration()
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);

        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Fact]
    public async Task Ticket_younger_than_eight_hours_remains_valid()
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            factory.Services,
            options,
            DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
    }

    [Fact]
    public async Task Ticket_at_least_eight_hours_old_is_rejected()
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            factory.Services,
            options,
            DateTimeOffset.UtcNow.AddHours(-8).AddMinutes(-1)
                .ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-time")]
    [InlineData("253402300800")]
    public async Task Missing_or_invalid_session_start_is_rejected(string? value)
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(factory.Services, options, value);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task Future_session_start_is_rejected()
    {
        using var factory = new WebPassFactory();
        var options = CookieOptions(factory.Services);
        var context = NewCookieContext(
            factory.Services,
            options,
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture));

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task Successful_login_writes_original_session_start_claim()
    {
        await using var db = NewDatabase();
        var hasher = new Argon2PasswordHasher();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("correct-password"),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var authentication = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var model = new LoginModel(new LoginService(db, hasher))
        {
            Input = new LoginModel.LoginInput
            {
                Username = "operator",
                Password = "correct-password",
            },
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    Connection =
                    {
                        RemoteIpAddress = IPAddress.Loopback,
                    },
                },
            },
        };
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.IsType<RedirectResult>(await model.OnPostAsync(default));

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var value = authentication.SignedInPrincipal!
            .FindFirstValue(LoginModel.SessionStartedClaimType);
        Assert.True(long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var started));
        Assert.InRange(started, before, after);
    }

    [Fact]
    public async Task Logout_writes_audit_and_signs_out_cookie_scheme()
    {
        await using var db = NewDatabase();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = "opaque-hash",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var authentication = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var model = new LogoutModel(new AuditWriter(db))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(
                            ClaimTypes.NameIdentifier,
                            user.Id.ToString())],
                        CookieAuthenticationDefaults.AuthenticationScheme)),
                },
            },
        };

        Assert.Equal(
            StatusCodes.Status405MethodNotAllowed,
            Assert.IsType<StatusCodeResult>(model.OnGet()).StatusCode);
        var result = await model.OnPostAsync(default);

        Assert.Equal("/login", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.SignedOutScheme);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("Logout", audit.Action);
        Assert.Null(audit.Details);
    }

    private static CookieAuthenticationOptions CookieOptions(
        IServiceProvider services) =>
        services.GetRequiredService<
                IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

    private static CookieValidatePrincipalContext NewCookieContext(
        IServiceProvider services,
        CookieAuthenticationOptions options,
        string? started)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (started is not null)
        {
            claims.Add(new(LoginModel.SessionStartedClaimType, started));
        }

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme,
            typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme)),
            scheme.Name);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        return new CookieValidatePrincipalContext(
            httpContext,
            scheme,
            options,
            ticket);
    }

    private static WebPassDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class RecordingAuthenticationService
        : IAuthenticationService
    {
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }
        public string? SignedOutScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            SignedOutScheme = scheme;
            return Task.CompletedTask;
        }
    }
}
```

If the .NET 10 `DefaultHttpContext.Connection` property cannot be assigned
inside the initializer, create the context first, set
`context.Connection.RemoteIpAddress = IPAddress.Loopback`, and then assign
it to `PageContext`. Do not change the assertion or production contract.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~AuthenticationSessionTests
```

Expected: compilation fails because `LoginModel.SessionStartedClaimType` and
`LogoutModel` do not exist; the Cookie options also lack the required
configuration.

- [ ] **Step 3: Add the original-login Claim**

In `Login.cshtml.cs`, add:

```csharp
using System.Globalization;
```

Inside `LoginModel`, add:

```csharp
public const string SessionStartedClaimType =
    "webpass:session-started-utc";
```

Change the successful principal construction to:

```csharp
var principal = new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim(
            SessionStartedClaimType,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture)),
    ],
    CookieAuthenticationDefaults.AuthenticationScheme));
```

Do not set `IsPersistent`; closing the browser may end the session earlier.

- [ ] **Step 4: Configure idle and absolute lifetimes in `Program.cs`**

Add:

```csharp
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using WebPass.Web.Pages;
```

Inside the existing `.AddCookie(options => { ... })` block, add:

```csharp
options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
options.SlidingExpiration = true;
options.Events.OnValidatePrincipal = async context =>
{
    var value = context.Principal?
        .FindFirst(LoginModel.SessionStartedClaimType)?
        .Value;
    var valid = long.TryParse(
        value,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var unixSeconds);
    DateTimeOffset startedAt = default;
    if (valid)
    {
        try
        {
            startedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            valid = false;
        }
    }

    var now = DateTimeOffset.UtcNow;
    if (!valid
        || startedAt > now
        || now - startedAt >= TimeSpan.FromHours(8))
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
    }
};
```

Keep the existing secure, HTTP-only, SameSite, login-path, and
access-denied settings unchanged.

- [ ] **Step 5: Add the POST-only logout page**

Create `Logout.cshtml`:

```html
@page "/logout"
@model WebPass.Web.Pages.LogoutModel
```

Create `Logout.cshtml.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Infrastructure.Auditing;

namespace WebPass.Web.Pages;

[Authorize]
public sealed class LogoutModel(AuditWriter auditWriter) : PageModel
{
    public IActionResult OnGet() =>
        StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var userId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await auditWriter.WriteAsync(
            new AuditEntry(
                userId,
                "Logout",
                "User",
                userId.ToString(),
                "Success",
                null),
            ct);
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }
}
```

- [ ] **Step 6: Add the authenticated navigation form**

In `_Layout.cshtml`, replace the final unauthenticated-only login link with:

```html
@if (hasUser)
{
    <form method="post" asp-page="/Logout">
        <button type="submit">Logout</button>
    </form>
}
else
{
    <a href="/login">Login</a>
}
```

Do not add a GET logout link.

- [ ] **Step 7: Run focused and security regression tests**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~AuthenticationSessionTests
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~Security
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj `
  -c Release --filter "FullyQualifiedName~Identity|FullyQualifiedName~Reauthentication"
```

Expected: all three commands pass with zero failures and skips.

- [ ] **Step 8: Review and commit Task 2**

Run:

```powershell
git diff --check
git status --short
```

Verify only the six Task 2 files are uncommitted. Stage and commit:

```powershell
git add -- src/WebPass.Web/Pages/Login.cshtml.cs `
  src/WebPass.Web/Program.cs `
  src/WebPass.Web/Pages/Logout.cshtml `
  src/WebPass.Web/Pages/Logout.cshtml.cs `
  src/WebPass.Web/Pages/Shared/_Layout.cshtml `
  tests/WebPass.IntegrationTests/Security/AuthenticationSessionTests.cs
git commit -m "feat: enforce session limits and add logout"
```

Stop at the checkpoint and obtain approval for Task 3.

---

### Task 3: Build and deploy the migration bundle

**Checkpoint estimate:** 3 files; 12k–18k tokens.

**Files:**
- Create: `scripts/Build-WebPassMigrationBundle.ps1`
- Modify: `docs/deployment/windows-server-iis.md`
- Create: `tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`

**Interfaces:**
- Produces:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>]
```

- Default output:
  `src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe`.
- Runtime bundle interface:

```powershell
.\WebPass.Migrations.exe --connection "<SQL Server connection string>"
```

- [ ] **Step 1: Write the failing end-to-end bundle test**

Create `MigrationBundleTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.IntegrationTests.Deployment;

public sealed class MigrationBundleTests
{
    [Fact]
    public async Task Script_builds_bundle_and_bundle_applies_all_migrations()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var script = Path.Combine(
            repositoryRoot,
            "scripts",
            "Build-WebPassMigrationBundle.ps1");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "WebPassMigrationBundleTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var bundle = Path.Combine(
            temporaryDirectory,
            "WebPass.Migrations.exe");
        var databaseName = "WebPassBundle_" + Guid.NewGuid().ToString("N");
        var connection =
            $"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer(connection)
            .Options;

        try
        {
            var build = await RunAsync(
                "powershell.exe",
                "-NoProfile",
                "-File",
                script,
                "-OutputPath",
                bundle);
            Assert.True(
                build.ExitCode == 0,
                $"Bundle build failed.{Environment.NewLine}{build.Error}{Environment.NewLine}{build.Output}");
            Assert.True(File.Exists(bundle), $"Bundle missing: {bundle}");

            var migrate = await RunAsync(
                bundle,
                "--connection",
                connection);
            Assert.True(
                migrate.ExitCode == 0,
                $"Bundle execution failed.{Environment.NewLine}{migrate.Error}{Environment.NewLine}{migrate.Output}");

            await using var db = new WebPassDbContext(options);
            Assert.Equal(
                (await db.Database.GetMigrationsAsync()).Order(),
                (await db.Database.GetAppliedMigrationsAsync()).Order());
        }
        finally
        {
            await using var cleanup = new WebPassDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(
            process.ExitCode,
            await output,
            await error);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Output,
        string Error);
}
```

The test writes only to an exact unique directory under the OS temporary
directory and deletes that exact directory in `finally`.

- [ ] **Step 2: Run the bundle test and verify RED**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~MigrationBundleTests
```

Expected: FAIL because
`scripts/Build-WebPassMigrationBundle.ps1` does not exist.

- [ ] **Step 3: Implement the repeatable PowerShell build script**

Create `scripts/Build-WebPassMigrationBundle.ps1`:

```powershell
[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..')).Path
$efTool = Join-Path $repositoryRoot '.tools\dotnet-ef.exe'
$webProject = Join-Path $repositoryRoot (
    'src\WebPass.Web\WebPass.Web.csproj')

if (-not (Test-Path -LiteralPath $efTool -PathType Leaf)) {
    throw "Repository-local EF tool was not found: $efTool"
}
if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "WebPass project was not found: $webProject"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot (
        'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe')
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force |
    Out-Null

& $efTool migrations bundle `
    --project $webProject `
    --startup-project $webProject `
    --configuration Release `
    --target-runtime win-x64 `
    --output $resolvedOutputPath `
    --force

if ($LASTEXITCODE -ne 0) {
    throw "Migration bundle build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
    throw "Migration bundle was not created: $resolvedOutputPath"
}

Write-Output $resolvedOutputPath
```

Do not use `--self-contained`; the deployment already requires the .NET 10
Hosting Bundle/runtime.

- [ ] **Step 4: Replace direct EF deployment with the bundle flow**

In section 3 of `docs/deployment/windows-server-iis.md`, after publishing the
website, replace the direct `dotnet ef database update` command with:

````markdown
Build the migration bundle from the same reviewed source commit and place it
in the staging directory:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

Apply migrations using a deployment identity that can alter the WebPass
database:

```powershell
C:\WebPass\staging\WebPass.Migrations.exe `
  --connection "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True"
```

Stop deployment if bundle creation or execution fails. Generate a new bundle
for every reviewed release; do not reuse a bundle from another source
version. The running WebPass website does not apply migrations automatically.
````

Retain the existing instruction to remove elevated database rights from the
runtime IIS identity after migration.

- [ ] **Step 5: Run the bundle test and verify GREEN**

Run the command from Step 2.

Expected: one test passes; it builds the framework-dependent `win-x64`
executable, applies all committed migrations to a unique SQL Server database,
and deletes the database and temporary directory.

- [ ] **Step 6: Verify the default output is ignored and repeatable**

Run:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1
.\scripts\Build-WebPassMigrationBundle.ps1
$bundle = 'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe'
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Missing bundle: $bundle"
}
git check-ignore -v -- $bundle
git status --short
```

Expected: both builds succeed, the file exists, `git check-ignore` reports
the `**/bin/` rule, and the generated executable does not appear in Git
status.

- [ ] **Step 7: Review and commit Task 3**

Run:

```powershell
git diff --check
git status --short
```

Stage only the script, deployment document, and bundle test:

```powershell
git add -- scripts/Build-WebPassMigrationBundle.ps1 `
  docs/deployment/windows-server-iis.md `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: add repeatable migration bundle deployment"
```

Stop at the checkpoint and obtain approval for Task 4.

---

### Task 4: Final regression and deployment verification

**Checkpoint estimate:** no planned source changes; 6k–10k tokens.

**Files:**
- Verify only; modify no file unless a verified defect requires returning to
  the owning task and repeating its RED/GREEN cycle.

**Interfaces:**
- Consumes all deliverables from Tasks 1–3.
- Produces final evidence and a clean implementation diff.

- [ ] **Step 1: Verify administrator behavior**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~AdminUsersTests
```

Expected: all create, reset, permission, enablement, audit, and concurrency
tests pass with zero failures and skips.

- [ ] **Step 2: Verify authentication behavior**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter "FullyQualifiedName~AuthenticationSessionTests|FullyQualifiedName~Security"
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj `
  -c Release --filter "FullyQualifiedName~Identity|FullyQualifiedName~Reauthentication"
```

Expected: all Cookie configuration, absolute lifetime, login, logout,
lockout, Argon2id, and reauthentication tests pass.

- [ ] **Step 3: Verify bundle generation and EF model stability**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~MigrationBundleTests
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release --no-build
```

Expected: bundle test passes and EF prints:

```text
No changes have been made to the model since the last migration.
```

- [ ] **Step 4: Run the full Release solution**

Run:

```powershell
dotnet test WebPass.sln -c Release
```

Expected: Unit and Integration projects pass with zero failures and zero
skips. Record the exact per-project and total counts.

- [ ] **Step 5: Audit the implementation against the design**

Run:

```powershell
git diff --check
git status --short
git log --oneline -4
git diff --name-only ccbdea3
```

Confirm:

- only the files listed in this plan changed after design commit `ccbdea3`;
- no entity, `WebPassDbContext`, snapshot, or migration file changed;
- no generated `.exe` is tracked;
- no total-user or total-administrator count check exists;
- no default password or hash appears in audit payload construction;
- existing authentication, authorization, Cookie security flags, lockout,
  Argon2id, and permission behavior remain covered by passing tests.

- [ ] **Step 6: Finish the branch**

Use `superpowers:verification-before-completion` before making any completion
claim. Then use `superpowers:finishing-a-development-branch` to detect the
base branch and offer local merge, push/PR, or keep-as-is. Do not merge,
push, delete the branch, or remove the worktree without the user's explicit
choice.

## Expected Commit Sequence

1. `feat: add ordinary user creation and password reset`
2. `feat: enforce session limits and add logout`
3. `feat: add repeatable migration bundle deployment`

The design and plan commits precede these implementation commits and remain
separate.
