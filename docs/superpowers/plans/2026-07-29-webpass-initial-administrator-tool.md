# WebPass Initial Administrator Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a separately published local console utility that creates an enabled WebPass administrator using the existing SQL Server model and Argon2id password hasher.

**Architecture:** A new `.NET 10` console project references `WebPass.Web` and contains a testable `AdministratorInitializer` plus a thin command-line application. The utility accepts a connection string and username, reads the password twice without echo, writes one `AppUser`, and never runs inside the web application.

**Tech Stack:** .NET 10, C#, EF Core 10 SQL Server and InMemory provider, existing WebPass Argon2id implementation, xUnit.

## Global Constraints

- Do not check whether `Users` is empty.
- Do not check whether another administrator exists.
- Each successful invocation creates one administrator with a distinct username.
- Do not add a web page, HTTP endpoint, startup hook, reset workflow, user-management feature, or administrator-count restriction.
- Reuse `WebPassDbContext`, `AppUser`, `IPasswordHasher`, and `Argon2PasswordHasher`; do not copy or replace password hashing.
- Never print or persist plaintext passwords outside `AppUser.PasswordHash`.
- Do not add `UserPermission` rows or change administrator authorization.
- Do not add an audit event or modify existing authentication, authorization, cookie, session, or audit code.
- Do not change the EF Core model or add a migration.
- Use TDD for behavior changes and finish each task with a focused commit.

---

## File Structure

- `src/WebPass.AdminInit/WebPass.AdminInit.csproj`: independently publishable console project referencing `WebPass.Web`.
- `src/WebPass.AdminInit/AdministratorInitializer.cs`: validation, duplicate detection, hashing, and the single user insert.
- `src/WebPass.AdminInit/Program.cs`: argument parsing, hidden password input, exit-code mapping, SQL Server context construction, and secret-free console output.
- `tests/WebPass.UnitTests/AdminInit/AdministratorInitializerTests.cs`: initializer behavior against an EF Core InMemory database plus the real Argon2id hasher.
- `tests/WebPass.UnitTests/AdminInit/AdminInitApplicationTests.cs`: command contract, prompt flow, output redaction, and exit codes.
- `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`: references the console project.
- `WebPass.sln`: includes the console project.
- `docs/deployment/windows-server-iis.md`: publish and local execution instructions.

### Task 1: Add the administrator initialization service

**Files:**
- Create: `src/WebPass.AdminInit/WebPass.AdminInit.csproj`
- Create: `src/WebPass.AdminInit/AdministratorInitializer.cs`
- Create: `tests/WebPass.UnitTests/AdminInit/AdministratorInitializerTests.cs`
- Modify: `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`
- Modify: `WebPass.sln`

**Interfaces:**
- Consumes: `WebPassDbContext`, `AppUser`, and `IPasswordHasher`.
- Produces:

```csharp
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
    public Task<AdministratorInitializationResult> CreateAsync(
        string? username,
        string? password,
        string? passwordConfirmation,
        CancellationToken ct);
}
```

- [ ] **Step 1: Add the project scaffold and test reference**

Create `src/WebPass.AdminInit/WebPass.AdminInit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WebPass.Web\WebPass.Web.csproj" />
  </ItemGroup>
</Project>
```

Add it to the solution:

```powershell
dotnet sln WebPass.sln add src\WebPass.AdminInit\WebPass.AdminInit.csproj
```

Add this project reference to `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`:

```xml
<ProjectReference Include="..\..\src\WebPass.AdminInit\WebPass.AdminInit.csproj" />
```

- [ ] **Step 2: Write the failing initializer tests**

Create `tests/WebPass.UnitTests/AdminInit/AdministratorInitializerTests.cs` with literal expectations:

```csharp
using Microsoft.EntityFrameworkCore;
using WebPass.AdminInit;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.AdminInit;

public sealed class AdministratorInitializerTests
{
    [Fact]
    public async Task Creates_enabled_administrator_with_verifiable_password()
    {
        await using var db = CreateDatabase();
        var hasher = new Argon2PasswordHasher();
        var initializer = new AdministratorInitializer(db, hasher);

        var result = await initializer.CreateAsync(
            "  deploy-admin  ",
            "local-admin-password",
            "local-admin-password",
            default);

        Assert.Equal(AdministratorInitializationResultKind.Created, result.Kind);
        Assert.Equal("deploy-admin", result.Username);
        var user = await db.Users.SingleAsync();
        Assert.Equal("deploy-admin", user.Username);
        Assert.True(user.IsAdministrator);
        Assert.True(user.IsEnabled);
        Assert.False(user.MustChangePassword);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.True(hasher.Verify("local-admin-password", user.PasswordHash));
        Assert.Empty(user.Permissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creates_when_existing_user_is_ordinary_or_administrator(
        bool existingIsAdministrator)
    {
        await using var db = CreateDatabase();
        db.Users.Add(new AppUser
        {
            Username = "existing",
            PasswordHash = "existing-hash",
            IsAdministrator = existingIsAdministrator,
        });
        await db.SaveChangesAsync();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            "new-admin",
            "new-password",
            "new-password",
            default);

        Assert.Equal(AdministratorInitializationResultKind.Created, result.Kind);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.True((await db.Users.SingleAsync(x => x.Username == "new-admin"))
            .IsAdministrator);
    }

    [Fact]
    public async Task Duplicate_username_does_not_create_another_user()
    {
        await using var db = CreateDatabase();
        db.Users.Add(new AppUser
        {
            Username = "admin",
            PasswordHash = "existing-hash",
            IsAdministrator = true,
        });
        await db.SaveChangesAsync();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            " admin ",
            "new-password",
            "new-password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.DuplicateUsername,
            result.Kind);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_username_does_not_write(string username)
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            username,
            "password",
            "password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.InvalidUsername,
            result.Kind);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Overlength_username_does_not_write()
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            new string('a', 129),
            "password",
            "password",
            default);

        Assert.Equal(
            AdministratorInitializationResultKind.InvalidUsername,
            result.Kind);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("", "", AdministratorInitializationResultKind.InvalidPassword)]
    [InlineData("one", "two", AdministratorInitializationResultKind.PasswordMismatch)]
    public async Task Invalid_password_input_does_not_write(
        string password,
        string confirmation,
        AdministratorInitializationResultKind expected)
    {
        await using var db = CreateDatabase();
        var initializer = new AdministratorInitializer(
            db,
            new Argon2PasswordHasher());

        var result = await initializer.CreateAsync(
            "admin",
            password,
            confirmation,
            default);

        Assert.Equal(expected, result.Kind);
        Assert.Empty(db.Users);
    }

    private static WebPassDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new WebPassDbContext(options);
    }
}
```

- [ ] **Step 3: Run the initializer tests to verify RED**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdministratorInitializerTests
```

Expected: FAIL to compile because `AdministratorInitializer`,
`AdministratorInitializationResult`, and
`AdministratorInitializationResultKind` do not exist.

- [ ] **Step 4: Implement the minimal initializer**

Create `src/WebPass.AdminInit/AdministratorInitializer.cs`:

```csharp
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
```

- [ ] **Step 5: Run the initializer tests to verify GREEN**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdministratorInitializerTests
```

Expected: PASS for administrator creation, existing ordinary/admin users,
duplicate username, username validation, and password validation.

- [ ] **Step 6: Run the existing identity tests**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter "FullyQualifiedName~Identity|FullyQualifiedName~AdministratorInitializerTests"
```

Expected: PASS with no change to Argon2id or login behavior.

- [ ] **Step 7: Commit Task 1**

```powershell
git add WebPass.sln src\WebPass.AdminInit tests\WebPass.UnitTests
git commit -m "feat: add administrator initialization service"
```

### Task 2: Add the interactive console command

**Files:**
- Modify: `src/WebPass.AdminInit/WebPass.AdminInit.csproj`
- Modify: `src/WebPass.AdminInit/Program.cs`
- Create: `tests/WebPass.UnitTests/AdminInit/AdminInitApplicationTests.cs`

**Interfaces:**
- Consumes: `AdministratorInitializer.CreateAsync`.
- Produces:

```csharp
public interface IAdminInitConsole
{
    string ReadSecret(string prompt);
    void WriteLine(string message);
}

public static class AdminInitApplication
{
    public static Task<int> RunAsync(
        string[] args,
        IAdminInitConsole console,
        Func<string, WebPassDbContext> dbFactory,
        CancellationToken ct);
}
```

- Exit codes: `0` created, `1` operational failure, `2` invalid usage/input,
  `3` duplicate username.

- [ ] **Step 1: Write the failing command tests**

Create `tests/WebPass.UnitTests/AdminInit/AdminInitApplicationTests.cs`. Use
a fake console that stores prompts and output but never echoes input:

```csharp
using Microsoft.EntityFrameworkCore;
using WebPass.AdminInit;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.UnitTests.AdminInit;

public sealed class AdminInitApplicationTests
{
    [Fact]
    public async Task Valid_command_creates_user_and_returns_zero()
    {
        var options = CreateOptions();
        var console = new FakeConsole("secret-value", "secret-value");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal("Administrator 'admin' created.", Assert.Single(console.Output));
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Single(verificationDb.Users);
        Assert.DoesNotContain(
            console.Output,
            line => line.Contains("secret-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "--username", "admin" })]
    [InlineData(new[] { "--connection-string", "test" })]
    [InlineData(new[] { "--unknown", "value" })]
    public async Task Invalid_arguments_return_two_without_opening_database(
        string[] args)
    {
        var console = new FakeConsole();
        var opened = false;

        var exitCode = await AdminInitApplication.RunAsync(
            args,
            console,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException();
            },
            default);

        Assert.Equal(2, exitCode);
        Assert.False(opened);
        Assert.Equal(
            "Usage: WebPass.AdminInit --connection-string <value> --username <value>",
            Assert.Single(console.Output));
    }

    [Fact]
    public async Task Unavailable_interactive_input_returns_two()
    {
        var console = new ThrowingConsole();
        var opened = false;

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException();
            },
            default);

        Assert.Equal(2, exitCode);
        Assert.False(opened);
        Assert.Equal(
            "Interactive password input is required.",
            Assert.Single(console.Output));
    }

    [Fact]
    public async Task Mismatched_password_returns_two_without_writing()
    {
        var options = CreateOptions();
        var console = new FakeConsole("secret-one", "secret-two");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(2, exitCode);
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Empty(verificationDb.Users);
        Assert.Equal("Password confirmation does not match.", Assert.Single(console.Output));
        Assert.DoesNotContain(
            console.Output,
            line => line.Contains("secret-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_username_returns_three()
    {
        var options = CreateOptions();
        var first = new FakeConsole("first-password", "first-password");
        var duplicate = new FakeConsole("second-password", "second-password");
        var args = new[]
        {
            "--connection-string", "test-connection",
            "--username", "admin",
        };
        Assert.Equal(
            0,
            await AdminInitApplication.RunAsync(
                args, first, _ => new WebPassDbContext(options), default));

        var exitCode = await AdminInitApplication.RunAsync(
            args,
            duplicate,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(3, exitCode);
        Assert.Equal("Username already exists.", Assert.Single(duplicate.Output));
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Single(verificationDb.Users);
    }

    [Fact]
    public async Task Operational_failure_returns_one_without_sensitive_details()
    {
        var console = new FakeConsole("secret-value", "secret-value");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "sensitive-connection",
                "--username", "admin",
            ],
            console,
            _ => throw new InvalidOperationException(
                "sensitive-connection secret-value"),
            default);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "Administrator creation failed. Verify database connectivity and permissions.",
            Assert.Single(console.Output));
    }

    private static DbContextOptions<WebPassDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private sealed class FakeConsole(params string[] secrets)
        : IAdminInitConsole
    {
        private readonly Queue<string> _secrets = new(secrets);
        public List<string> Output { get; } = [];

        public string ReadSecret(string prompt)
        {
            Assert.Equal(
                _secrets.Count == secrets.Length
                    ? "Password: "
                    : "Confirm password: ",
                prompt);
            return _secrets.Dequeue();
        }

        public void WriteLine(string message) => Output.Add(message);
    }

    private sealed class ThrowingConsole : IAdminInitConsole
    {
        public List<string> Output { get; } = [];

        public string ReadSecret(string prompt) =>
            throw new InvalidOperationException("No interactive input.");

        public void WriteLine(string message) => Output.Add(message);
    }
}
```

- [ ] **Step 2: Run the command tests to verify RED**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInitApplicationTests
```

Expected: FAIL because `IAdminInitConsole` and `AdminInitApplication` do not
exist.

- [ ] **Step 3: Implement argument parsing and exit-code mapping**

Add the executable output type to
`src/WebPass.AdminInit/WebPass.AdminInit.csproj`:

```xml
<OutputType>Exe</OutputType>
```

Create `src/WebPass.AdminInit/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.AdminInit;

public interface IAdminInitConsole
{
    string ReadSecret(string prompt);
    void WriteLine(string message);
}

public static class AdminInitApplication
{
    private const string Usage =
        "Usage: WebPass.AdminInit --connection-string <value> --username <value>";

    public static async Task<int> RunAsync(
        string[] args,
        IAdminInitConsole console,
        Func<string, WebPassDbContext> dbFactory,
        CancellationToken ct)
    {
        if (!TryParse(args, out var connectionString, out var username))
        {
            console.WriteLine(Usage);
            return 2;
        }

        string password;
        string confirmation;
        try
        {
            password = console.ReadSecret("Password: ");
            confirmation = console.ReadSecret("Confirm password: ");
        }
        catch (InvalidOperationException)
        {
            console.WriteLine(
                "Interactive password input is required.");
            return 2;
        }

        try
        {
            await using var db = dbFactory(connectionString);
            var initializer = new AdministratorInitializer(
                db,
                new Argon2PasswordHasher());
            var result = await initializer.CreateAsync(
                username,
                password,
                confirmation,
                ct);
            return WriteResult(console, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            console.WriteLine(
                "Administrator creation failed. Verify database connectivity and permissions.");
            return 1;
        }
    }

    private static bool TryParse(
        string[] args,
        out string connectionString,
        out string username)
    {
        connectionString = string.Empty;
        username = string.Empty;
        if (args.Length != 4)
        {
            return false;
        }

        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] is not ("--connection-string" or "--username")
                || string.IsNullOrEmpty(args[index + 1])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue(
                "--connection-string",
                out connectionString)
            || !values.TryGetValue("--username", out username))
        {
            return false;
        }

        username = username.Trim();
        return username.Length is > 0 and <= 128;
    }

    private static int WriteResult(
        IAdminInitConsole console,
        AdministratorInitializationResult result)
    {
        switch (result.Kind)
        {
            case AdministratorInitializationResultKind.Created:
                console.WriteLine(
                    $"Administrator '{result.Username}' created.");
                return 0;
            case AdministratorInitializationResultKind.DuplicateUsername:
                console.WriteLine("Username already exists.");
                return 3;
            case AdministratorInitializationResultKind.InvalidUsername:
                console.WriteLine(
                    "Username must contain 1 to 128 characters.");
                return 2;
            case AdministratorInitializationResultKind.InvalidPassword:
                console.WriteLine("Password must not be empty.");
                return 2;
            case AdministratorInitializationResultKind.PasswordMismatch:
                console.WriteLine(
                    "Password confirmation does not match.");
                return 2;
            default:
                throw new InvalidOperationException(
                    "Unknown initialization result.");
        }
    }
}

public sealed class SystemAdminInitConsole : IAdminInitConsole
{
    public string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Redirected password input is not supported.");
        }

        Console.Write(prompt);
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. characters]);
            }

            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }
    }

    public void WriteLine(string message) => Console.WriteLine(message);
}

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        AdminInitApplication.RunAsync(
            args,
            new SystemAdminInitConsole(),
            connectionString =>
            {
                var options =
                    new DbContextOptionsBuilder<WebPassDbContext>()
                        .UseSqlServer(connectionString)
                        .Options;
                return new WebPassDbContext(options);
            },
            CancellationToken.None);
}
```

- [ ] **Step 4: Run the command tests to verify GREEN**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInitApplicationTests
```

Expected: PASS for success, invalid arguments, mismatched confirmation,
duplicate username, redacted output, and stable exit codes.

- [ ] **Step 5: Run all administrator initialization tests**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

Expected: PASS.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src\WebPass.AdminInit\Program.cs tests\WebPass.UnitTests\AdminInit
git commit -m "feat: add administrator initialization command"
```

### Task 3: Document, publish, and verify the utility

**Files:**
- Modify: `docs/deployment/windows-server-iis.md`

**Interfaces:**
- Consumes: `WebPass.AdminInit.exe` from Tasks 1–2.
- Produces: a deployment runbook section with exact publish and execution
  commands.

- [ ] **Step 1: Add the deployment runbook section**

Append a section before production checks in
`docs/deployment/windows-server-iis.md`:

````markdown
## Create an administrator

Publish the local initialization utility separately:

```powershell
dotnet publish src\WebPass.AdminInit -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\AdminInit
```

Run it locally with a deployment identity that can insert into the WebPass
database:

```powershell
C:\WebPass\AdminInit\WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

Enter and confirm the password at the hidden prompts. The command does not
check whether users or administrators already exist; every successful
invocation creates another administrator with the requested distinct
username.

The utility is not required by the running website. Delete
`C:\WebPass\AdminInit` after use if operators do not need to retain it.
````

Renumber following runbook sections so headings remain sequential.

- [ ] **Step 2: Run the focused tests**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

Expected: PASS.

- [ ] **Step 3: Publish the utility**

Run:

```powershell
dotnet publish src\WebPass.AdminInit\WebPass.AdminInit.csproj -c Release -r win-x64 --self-contained false
```

Expected: PASS and
`src/WebPass.AdminInit/bin/Release/net10.0/win-x64/publish/WebPass.AdminInit.exe`
exists.

- [ ] **Step 4: Verify no EF migration is required**

Run with the repository-local EF tool:

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release --no-build
```

Expected: `No changes have been made to the model since the last migration.`

- [ ] **Step 5: Run the full Release suite**

Run:

```powershell
dotnet test WebPass.sln -c Release
```

Expected: all unit and integration tests pass with zero failures and zero
skips.

- [ ] **Step 6: Check the final diff**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only approved administrator-tool, solution,
test-project, test, and deployment-document files are modified.

- [ ] **Step 7: Commit Task 3**

```powershell
git add docs\deployment\windows-server-iis.md
git commit -m "docs: add administrator initialization instructions"
```

## Final Review Checklist

- [ ] The tool does not inspect total user or administrator counts.
- [ ] Existing ordinary users and administrators do not block creation.
- [ ] Duplicate usernames are rejected without another row.
- [ ] Password input is hidden, confirmed, Argon2id-hashed, and never printed.
- [ ] Created users are enabled administrators with no `UserPermission` rows.
- [ ] The web application has no initializer endpoint or startup behavior.
- [ ] Authentication, authorization, cookies, sessions, and audit code are
  unchanged.
- [ ] EF reports no pending model changes.
- [ ] The utility publishes independently for `win-x64`.
- [ ] The full Release suite passes.
