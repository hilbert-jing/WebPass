# WebPass Core Platform Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the testable WebPass core: local login, per-user permissions, IPv4 CIDR and server inventory, Ping, status updates, and audit.

**Architecture:** ASP.NET Core 10 Razor Pages/MVC is an IIS-hosted modular monolith using SQL Server 2025 Express. Cookie authentication contains only the user identifier; authorization loads current permissions from the database. Domain services own IP validation, asset commands, Ping and auditing while page handlers remain thin.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages/MVC, EF Core SQL Server, SQL Server 2025 Express, Argon2id, xUnit, Microsoft.AspNetCore.Mvc.Testing.

## Global Constraints

- Use a 64-bit IIS-hosted ASP.NET Core 10 application and SQL Server 2025 Express.
- First release supports IPv4 CIDR only; no external service dependency.
- Default server ordering is numeric business-IP ascending.
- Ping is on demand and never changes manual status automatically.
- Use local accounts; administrators have all capabilities and ordinary users have individually stored permissions.
- Audit login, permission, subnet, asset, Ping and manual-status operations without secrets.
- Each task follows TDD and ends with a focused commit.

---

## File Structure

- `src/WebPass.Web/Domain/`: entities and enums.
- `src/WebPass.Web/Application/`: contracts and use cases.
- `src/WebPass.Web/Infrastructure/`: EF Core, Argon2id, Ping, authorization, audit.
- `src/WebPass.Web/Pages/`: Razor pages and handlers.
- `tests/WebPass.UnitTests/`: domain/service tests.
- `tests/WebPass.IntegrationTests/`: database and HTTP tests.

### Task 1: Scaffold the web application and validate runtime options

**Files:**
- Create: `WebPass.sln`
- Create: `src/WebPass.Web/WebPass.Web.csproj`
- Create: `src/WebPass.Web/Program.cs`
- Create: `src/WebPass.Web/appsettings.json`
- Create: `src/WebPass.Web/Configuration/WebPassOptions.cs`
- Create: `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`
- Create: `tests/WebPass.UnitTests/Configuration/WebPassOptionsTests.cs`
- Create: `tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj`
- Create: `tests/WebPass.IntegrationTests/WebPassFactory.cs`

**Interfaces:**
- Produces `WebPassOptions { int PingTimeoutMilliseconds; int PingMaxConcurrency; int PingPerUserPerMinute; }`.
- Produces startup validation through `IValidateOptions<WebPassOptions>`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Rejects_non_positive_ping_timeout()
{
    var result = new WebPassOptionsValidator().Validate(null,
        new WebPassOptions { PingTimeoutMilliseconds = 0, PingMaxConcurrency = 2, PingPerUserPerMinute = 5 });
    Assert.False(result.Succeeded);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~WebPassOptionsTests`

Expected: FAIL because `WebPassOptionsValidator` does not exist.

- [ ] **Step 3: Implement the minimal configuration boundary**

```csharp
public sealed class WebPassOptionsValidator : IValidateOptions<WebPassOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPassOptions value) =>
        value.PingTimeoutMilliseconds > 0 && value.PingMaxConcurrency > 0 && value.PingPerUserPerMinute > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Ping values must be positive.");
}
```

Register `AddOptions<WebPassOptions>().BindConfiguration("WebPass").ValidateOnStart()`, Razor Pages, antiforgery, cookie authentication, SQL Server DbContext and the two test projects.

- [ ] **Step 4: Run the tests and build**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~WebPassOptionsTests`

Expected: PASS.

Run: `dotnet build WebPass.sln -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WebPass.sln src/WebPass.Web tests
git commit -m "chore: scaffold WebPass application"
```

### Task 2: Add EF Core entities, migrations, local authentication and audit

**Files:**
- Create: `src/WebPass.Web/Domain/Entities/AppUser.cs`
- Create: `src/WebPass.Web/Domain/Entities/UserPermission.cs`
- Create: `src/WebPass.Web/Domain/Entities/Subnet.cs`
- Create: `src/WebPass.Web/Domain/Entities/ServerAsset.cs`
- Create: `src/WebPass.Web/Domain/Entities/PingResult.cs`
- Create: `src/WebPass.Web/Domain/Entities/AuditLog.cs`
- Create: `src/WebPass.Web/Domain/Enums/AliveStatus.cs`
- Create: `src/WebPass.Web/Data/WebPassDbContext.cs`
- Create: `src/WebPass.Web/Data/Migrations/202607240001_InitialCore.cs`
- Create: `src/WebPass.Web/Infrastructure/Identity/Argon2PasswordHasher.cs`
- Create: `src/WebPass.Web/Infrastructure/Identity/LoginService.cs`
- Create: `src/WebPass.Web/Infrastructure/Auditing/AuditWriter.cs`
- Create: `src/WebPass.Web/Pages/Login.cshtml`
- Create: `src/WebPass.Web/Pages/Login.cshtml.cs`
- Test: `tests/WebPass.IntegrationTests/Data/CorePersistenceTests.cs`
- Test: `tests/WebPass.UnitTests/Identity/LoginServiceTests.cs`

**Interfaces:**
- Produces `Task<LoginResult> LoginAsync(string username, string password, IPAddress sourceIp, CancellationToken ct)`.
- Produces `Task WriteAsync(AuditEntry entry, CancellationToken ct)`.
- Produces active-IP uniqueness and `rowversion` concurrency for `ServerAsset`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Duplicate_active_business_ip_is_rejected()
{
    db.ServerAssets.AddRange(NewAsset("10.0.0.1"), NewAsset("10.0.0.1"));
    await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
}

[Fact]
public async Task Five_failed_logins_lock_the_user()
{
    var result = await service.LoginAsync("operator", "wrong", IPAddress.Loopback, default);
    Assert.Equal(LoginResultKind.Locked, result.Kind);
}
```

- [ ] **Step 2: Run focused tests to verify failure**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~LoginServiceTests`

Expected: FAIL because login services are absent.

- [ ] **Step 3: Implement persistence, hashing, cookie sign-in and audit redaction**

```csharp
builder.Entity<ServerAsset>().HasIndex(x => x.BusinessIp).IsUnique().HasFilter("[IsArchived] = 0");
builder.Entity<ServerAsset>().Property(x => x.RowVersion).IsRowVersion();
builder.Entity<UserPermission>().HasIndex(x => new { x.UserId, x.PermissionCode }).IsUnique();
```

Give `ServerAsset` `BusinessIpNumber: long`, `IsArchived`, timestamps and actor IDs. Hash with random Argon2id salt; lock after five failures; cookie claim is only `ClaimTypes.NameIdentifier`. `AuditWriter` must reject payload property names containing `password`, `secret`, `ciphertext`, `token`, `cookie`, `authorization` or `key`.

- [ ] **Step 4: Apply migration and run tests**

Run: `dotnet ef database update --project src/WebPass.Web --startup-project src/WebPass.Web`

Expected: migration applies.

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~LoginServiceTests`

Expected: PASS.

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~CorePersistenceTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Domain src/WebPass.Web/Data src/WebPass.Web/Infrastructure src/WebPass.Web/Pages/Login* tests
git commit -m "feat: add core data and local authentication"
```

### Task 3: Implement per-user authorization and CIDR subnet management

**Files:**
- Create: `src/WebPass.Web/Application/Authorization/PermissionCode.cs`
- Create: `src/WebPass.Web/Infrastructure/Authorization/PermissionAuthorizationHandler.cs`
- Create: `src/WebPass.Web/Application/Networking/Ipv4Cidr.cs`
- Create: `src/WebPass.Web/Application/Subnets/SubnetService.cs`
- Create: `src/WebPass.Web/Pages/Subnets/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Subnets/Index.cshtml.cs`
- Create: `src/WebPass.Web/Pages/Admin/Users.cshtml`
- Create: `src/WebPass.Web/Pages/Admin/Users.cshtml.cs`
- Test: `tests/WebPass.UnitTests/Networking/Ipv4CidrTests.cs`
- Test: `tests/WebPass.IntegrationTests/Authorization/PermissionTests.cs`

**Interfaces:**
- Produces `PermissionCode.AssetView, AssetCreate, AssetEdit, AssetArchive, PingExecute, StatusMarkAlive, ImportData, ExportData, SecretReveal, SubnetManage, AuditView`.
- Produces `Ipv4Cidr.Parse(string)`, `ContainsUsable(IPAddress)`, `GetUsableAddressCount()`, `EnumerateUsableAddresses(int skip, int take)`.

- [ ] **Step 1: Write failing authorization and CIDR tests**

```csharp
[Fact]
public void Slash24_has_254_usable_addresses()
{
    var cidr = Ipv4Cidr.Parse("10.0.0.0/24");
    Assert.Equal(254, cidr.GetUsableAddressCount());
    Assert.False(cidr.ContainsUsable(IPAddress.Parse("10.0.0.255")));
}

[Fact]
public async Task User_without_asset_create_is_forbidden()
{
    Assert.False(await handler.IsAllowedAsync(userId, PermissionCode.AssetCreate, default));
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~Ipv4CidrTests`

Expected: FAIL because CIDR code does not exist.

- [ ] **Step 3: Implement permission handler, subnet service and pages**

```csharp
public sealed record PermissionRequirement(PermissionCode Code) : IAuthorizationRequirement;
public sealed record SubnetInput(string Name, string Cidr, string Location, string? Notes, bool IsEnabled);
```

Administrators bypass individual permission rows; ordinary users are checked from `UserPermissions` on each request. Reject IPv6, malformed CIDR, overlapping ranges, network/broadcast addresses, and deletion of a subnet with active assets. The admin page may grant only the approved ordinary-user permissions and must reject editing administrator grants.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~Ipv4CidrTests`

Expected: PASS.

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~PermissionTests`

Expected: PASS for administrator bypass and ordinary-user denial.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Application src/WebPass.Web/Infrastructure/Authorization src/WebPass.Web/Pages/Subnets src/WebPass.Web/Pages/Admin tests
git commit -m "feat: add permissions and subnet management"
```

### Task 4: Implement asset CRUD, dynamic address pool, Ping and core UI

**Files:**
- Create: `src/WebPass.Web/Application/Assets/ServerAssetInput.cs`
- Create: `src/WebPass.Web/Application/Assets/ServerAssetService.cs`
- Create: `src/WebPass.Web/Application/Ping/PingService.cs`
- Create: `src/WebPass.Web/Infrastructure/Networking/SystemPingTransport.cs`
- Create: `src/WebPass.Web/Pages/Servers/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Servers/Index.cshtml.cs`
- Create: `src/WebPass.Web/Pages/Servers/Edit.cshtml`
- Create: `src/WebPass.Web/Pages/Servers/Edit.cshtml.cs`
- Create: `src/WebPass.Web/Pages/Servers/Ping.cshtml.cs`
- Create: `src/WebPass.Web/Pages/Audit/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Audit/Index.cshtml.cs`
- Test: `tests/WebPass.IntegrationTests/Assets/AssetAndPingTests.cs`

**Interfaces:**
- Produces `CreateAsync(ServerAssetInput, Guid, CancellationToken)`, `UpdateAsync(Guid, ServerAssetInput, byte[], Guid, CancellationToken)`, and `ListAsync(ServerListQuery, CancellationToken)`.
- Produces `ExecuteAsync(Guid assetId, Guid actorUserId, CancellationToken)` and `MarkAliveAsync(Guid assetId, Guid actorUserId, byte[] rowVersion, CancellationToken)`.

- [ ] **Step 1: Write failing asset, ordering and Ping tests**

```csharp
[Fact]
public async Task List_orders_10_0_0_9_before_10_0_0_10()
{
    var page = await assets.ListAsync(new ServerListQuery(), default);
    Assert.Equal(new[] { "10.0.0.9", "10.0.0.10" }, page.Items.Select(x => x.BusinessIp));
}

[Fact]
public async Task Ping_success_does_not_change_manual_status()
{
    await ping.ExecuteAsync(asset.Id, operatorId, default);
    Assert.Equal(AliveStatus.Unknown, (await db.ServerAssets.FindAsync(asset.Id))!.AliveStatus);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~AssetAndPingTests`

Expected: FAIL because asset and Ping services do not exist.

- [ ] **Step 3: Implement services and Razor handlers**

```csharp
public sealed record ServerAssetInput(string BusinessIp, string Location, AliveStatus AliveStatus,
    string ComputerName, string SystemName, string? OperatingSystemVersion,
    string? DatabaseVersion, string? Notes);
```

Convert IPv4 to unsigned 32-bit `long`, require an enabled containing subnet, use active-IP uniqueness, and audit each command. The list defaults to `OrderBy(x => x.BusinessIpNumber)`; the full-pool view calls `EnumerateUsableAddresses(skip, take)` and overlays registered assets instead of creating empty database rows. Ping uses `System.Net.NetworkInformation.Ping` behind an interface, fixed timeout, `SemaphoreSlim` global limit, per-user rate limit, allowed subnet check, result persistence and audit. Only the separate `MarkAliveAsync` endpoint updates status.

- [ ] **Step 4: Run tests and application smoke test**

Run: `dotnet test WebPass.sln -c Release`

Expected: PASS.

Run: `dotnet run --project src/WebPass.Web`

Expected: application starts with `/login`, `/servers`, `/subnets`, `/audit` and `/admin/users`.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Application/Assets src/WebPass.Web/Application/Ping src/WebPass.Web/Infrastructure/Networking src/WebPass.Web/Pages/Servers src/WebPass.Web/Pages/Audit tests
git commit -m "feat: add audited server inventory and ping"
```

## Self-Review

- Coverage: the four tasks implement the full core phase: local authentication, user-level permissions, audit, IPv4 CIDR, dynamic address pools, asset CRUD, numeric ordering, Ping and separate manual status.
- Plan boundary: password encryption/reveal, file import/export, encrypted backup and IIS production runbooks are implemented by the secure-data plan.
- Consistency: `PermissionCode`, `Ipv4Cidr`, `ServerAssetInput`, `ServerAssetService` and `PingService` are defined before use.
