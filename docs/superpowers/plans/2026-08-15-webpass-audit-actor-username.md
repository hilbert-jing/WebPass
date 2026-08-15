# WebPass Audit Actor Username Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store and display audit actor username snapshots while retaining stable actor IDs and safely backfilling resolvable history.

**Architecture:** Extend the audit entity with a nullable snapshot field, resolve it centrally in `AuditWriter`, and keep page fallback behavior explicit. Add one additive SQL Server migration with a tolerant joined backfill and no user foreign key.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, EF Core 10, SQL Server, xUnit

## Global Constraints

- Change only the audit feature; do not change login, passwords, permissions, secrets, ping, server lists, or unrelated behavior.
- Keep `ActorUserId`; never replace or remove the stable user ID.
- System and unresolved historical events must remain readable and must not make migration fail.
- Keep tests to the minimum behavior needed for this change.

---

### Task 1: Specify actor snapshot and display behavior with focused tests

**Files:**
- Modify: `tests/WebPass.UnitTests/Auditing/AuditWriterTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Consumes: existing `AuditWriter.WriteAsync(AuditEntry, CancellationToken)` and `/audit`
- Produces: expectations for `AuditLog.ActorUsername` and the three actor display branches

- [ ] **Step 1: Add one writer test before production code**

Add a test that creates one real `AppUser`, writes a resolved audit entry and an entry with a non-existent actor ID, and expects the first row to contain both ID and username while the second row retains its ID with a null username.

```csharp
[Fact]
public async Task Writes_username_snapshot_without_requiring_a_matching_user()
{
    // Arrange a real in-memory DbContext and AppUser("operator").
    // Write one entry for that user and one for an orphaned Guid.
    // Assert literal "operator", both stable IDs, and null for the orphan snapshot.
}
```

- [ ] **Step 2: Extend the existing governance page fixture**

Seed rows with `ActorUsername = "presentation-user"`, a null actor, and an orphaned actor ID. Assert the page contains the username, `系统`, and `未知用户（<literal ID>）`, and does not render the resolved actor's GUID.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AuditWriterTests
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~Governance_pages_render_chinese_read_only_and_permission_management_contracts
```

Expected: compilation or assertion failure because `ActorUsername` and the new display behavior do not exist.

### Task 2: Implement the snapshot model, writer, and page

**Files:**
- Modify: `src/WebPass.Web/Domain/Entities/AuditLog.cs`
- Modify: `src/WebPass.Web/Data/WebPassDbContext.cs`
- Modify: `src/WebPass.Web/Infrastructure/Auditing/AuditWriter.cs`
- Modify: `src/WebPass.Web/Pages/Audit/Index.cshtml`

**Interfaces:**
- Consumes: `AuditEntry.ActorUserId`, `WebPassDbContext.Users`
- Produces: nullable `AuditLog.ActorUsername` persisted with every audit row

- [ ] **Step 1: Add the entity property and EF configuration**

```csharp
public string? ActorUsername { get; set; }
```

```csharp
entity.Property(x => x.ActorUsername).HasMaxLength(128);
```

- [ ] **Step 2: Resolve the snapshot centrally**

Before adding the audit entity, query only the username when `ActorUserId` is present. Use `SingleOrDefaultAsync` so no matching user returns null without failing.

```csharp
var actorUsername = entry.ActorUserId is { } actorUserId
    ? await db.Users.AsNoTracking()
        .Where(user => user.Id == actorUserId)
        .Select(user => user.Username)
        .SingleOrDefaultAsync(ct)
    : null;
```

Assign `ActorUsername = actorUsername` without changing `ActorUserId`.

- [ ] **Step 3: Render the explicit fallback order**

```csharp
@(string.IsNullOrWhiteSpace(entry.ActorUsername)
    ? entry.ActorUserId is { } actorUserId
        ? $"未知用户（{actorUserId}）"
        : "系统"
    : entry.ActorUsername)
```

- [ ] **Step 4: Run the two focused tests and verify GREEN**

Run the commands from Task 1. Expected: both pass.

### Task 3: Add and prove the production-history migration

**Files:**
- Create: EF-generated `src/WebPass.Web/Data/Migrations/*_AddAuditActorUsername.cs`
- Create: EF-generated `src/WebPass.Web/Data/Migrations/*_AddAuditActorUsername.Designer.cs`
- Modify: `src/WebPass.Web/Data/Migrations/WebPassDbContextModelSnapshot.cs`
- Create: `tests/WebPass.IntegrationTests/Data/AuditActorUsernameMigrationTests.cs`

**Interfaces:**
- Consumes: previous migration `20260726131039_AddImportJobs`, `AuditLogs.ActorUserId`, `Users.Id`, `Users.Username`
- Produces: nullable `nvarchar(128)` `AuditLogs.ActorUsername` with resolvable history backfilled

- [ ] **Step 1: Write one SQL Server migration test before creating the migration**

Migrate a unique test database to `20260726131039_AddImportJobs`, insert one user plus matched, orphaned, and system audit rows using SQL valid for that old schema, migrate to latest, and assert literal results:

```csharp
Assert.Equal("historical-operator", matched.ActorUsername);
Assert.Equal(matchedActorId, matched.ActorUserId);
Assert.Null(orphan.ActorUsername);
Assert.Equal(orphanActorId, orphan.ActorUserId);
Assert.Null(system.ActorUsername);
Assert.Null(system.ActorUserId);
```

- [ ] **Step 2: Run the migration test and verify RED**

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~AuditActorUsernameMigrationTests
```

Expected: failure because the latest migration does not yet add or backfill `ActorUsername`.

- [ ] **Step 3: Generate the EF migration**

```powershell
dotnet ef migrations add AddAuditActorUsername --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
```

- [ ] **Step 4: Add the tolerant backfill to `Up`**

Keep the generated nullable column and add:

```csharp
migrationBuilder.Sql(
    """
    UPDATE [audit]
    SET [audit].[ActorUsername] = [users].[Username]
    FROM [AuditLogs] AS [audit]
    INNER JOIN [Users] AS [users]
        ON [audit].[ActorUserId] = [users].[Id]
    WHERE [audit].[ActorUsername] IS NULL;
    """);
```

Do not add a foreign key, default, index, or non-null constraint. `Down` drops only `ActorUsername`.

- [ ] **Step 5: Run the migration test and verify GREEN**

Run the command from Step 2. Expected: pass.

### Task 4: Verify the complete scoped change

**Files:**
- Verify only; no planned source additions

**Interfaces:**
- Consumes: all implementation and tests above
- Produces: fresh build, test, migration, and diff evidence

- [ ] **Step 1: Check migration/model consistency**

```powershell
dotnet ef migrations has-pending-model-changes --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
```

Expected: no pending model changes.

- [ ] **Step 2: Run the scoped tests and solution build**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AuditWriterTests
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Governance_pages_render_chinese_read_only_and_permission_management_contracts|FullyQualifiedName~AuditActorUsernameMigrationTests"
dotnet build WebPass.sln -c Release --no-restore
```

Expected: zero failures and build exit code 0.

- [ ] **Step 3: Review scope and generated SQL**

```powershell
dotnet ef migrations script 20260726131039_AddImportJobs --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
git diff --check
git status --short
git diff -- src/WebPass.Web tests/WebPass.UnitTests/Auditing tests/WebPass.IntegrationTests/Presentation tests/WebPass.IntegrationTests/Data docs/superpowers
```

Confirm the script adds a nullable `nvarchar(128)` column, updates only matching users, and contains no audit-user foreign key. Confirm no unrelated feature files changed.
