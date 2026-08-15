# WebPass 审计操作人用户名实施计划

> **面向代理执行者：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务实施。步骤使用复选框（`- [ ]`）跟踪。

**目标：** 保存并显示审计操作人用户名快照，同时保留稳定操作人 ID，并安全回填能够解析的历史数据。

**架构：** 为审计实体增加可空快照字段，在 `AuditWriter` 中集中解析，并明确页面降级顺序。新增一个可追加的 SQL Server migration，通过容错联表更新回填，不添加用户外键。

**技术栈：** .NET 10、ASP.NET Core Razor Pages、EF Core 10、SQL Server、xUnit

## 全局约束

- 只修改审计功能；不得修改登录、密码、权限、Secret、Ping、服务器列表或无关行为。
- 保留 `ActorUserId`，不得替换或删除稳定用户 ID。
- 系统事件和无法解析的历史事件必须继续可读，且不得使 migration 失败。
- 测试仅保留本次变更所必需的行为覆盖。

---

### 任务 1：用聚焦测试规定快照与显示行为

**文件：**
- 修改：`tests/WebPass.UnitTests/Auditing/AuditWriterTests.cs`
- 修改：`tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**接口：**
- 输入：现有 `AuditWriter.WriteAsync(AuditEntry, CancellationToken)` 和 `/audit`
- 输出：`AuditLog.ActorUsername` 及三种操作人显示分支的期望行为

- [ ] **步骤 1：先增加一个写入器测试**

使用真实内存数据库创建 `AppUser("operator")`，分别写入能够解析和用户不存在的审计记录；断言第一条同时保存 ID 和用户名，第二条保留 ID 且用户名为空。

```csharp
[Fact]
public async Task Writes_username_snapshot_without_requiring_a_matching_user()
{
    // 创建真实 DbContext 和用户，写入已解析及孤立 ID 两条记录。
    // 断言字面量 "operator"、两个稳定 ID，以及孤立记录的空快照。
}
```

- [ ] **步骤 2：扩展现有治理页面测试数据**

分别种入 `ActorUsername = "presentation-user"`、空操作人及孤立操作人 ID 的记录；断言页面包含用户名、“系统”和“未知用户（<固定 ID>）”，且不渲染已解析操作人的 GUID。

- [ ] **步骤 3：运行聚焦测试并确认 RED**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AuditWriterTests
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~Governance_pages_render_chinese_read_only_and_permission_management_contracts
```

预期：因 `ActorUsername` 和新显示行为尚不存在而编译或断言失败。

### 任务 2：实现快照模型、集中写入和页面显示

**文件：**
- 修改：`src/WebPass.Web/Domain/Entities/AuditLog.cs`
- 修改：`src/WebPass.Web/Data/WebPassDbContext.cs`
- 修改：`src/WebPass.Web/Infrastructure/Auditing/AuditWriter.cs`
- 修改：`src/WebPass.Web/Pages/Audit/Index.cshtml`

**接口：**
- 输入：`AuditEntry.ActorUserId`、`WebPassDbContext.Users`
- 输出：随每条审计记录保存的可空 `AuditLog.ActorUsername`

- [ ] **步骤 1：增加实体属性和 EF 配置**

```csharp
public string? ActorUsername { get; set; }
```

```csharp
entity.Property(x => x.ActorUsername).HasMaxLength(128);
```

- [ ] **步骤 2：集中解析用户名快照**

增加审计实体前，仅在 `ActorUserId` 存在时查询用户名。使用 `SingleOrDefaultAsync`，确保用户不存在时返回空值而不失败。

```csharp
var actorUsername = entry.ActorUserId is { } actorUserId
    ? await db.Users.AsNoTracking()
        .Where(user => user.Id == actorUserId)
        .Select(user => user.Username)
        .SingleOrDefaultAsync(ct)
    : null;
```

赋值 `ActorUsername = actorUsername`，不得改变 `ActorUserId`。

- [ ] **步骤 3：渲染明确的降级顺序**

```csharp
@(string.IsNullOrWhiteSpace(entry.ActorUsername)
    ? entry.ActorUserId is { } actorUserId
        ? $"未知用户（{actorUserId}）"
        : "系统"
    : entry.ActorUsername)
```

- [ ] **步骤 4：运行任务 1 的两个测试并确认 GREEN**

预期：两个测试均通过。

### 任务 3：增加并验证生产历史 migration

**文件：**
- 创建：EF 生成的 `src/WebPass.Web/Data/Migrations/*_AddAuditActorUsername.cs`
- 创建：EF 生成的 `src/WebPass.Web/Data/Migrations/*_AddAuditActorUsername.Designer.cs`
- 修改：`src/WebPass.Web/Data/Migrations/WebPassDbContextModelSnapshot.cs`
- 创建：`tests/WebPass.IntegrationTests/Data/AuditActorUsernameMigrationTests.cs`

**接口：**
- 输入：上一 migration `20260726131039_AddImportJobs`、`AuditLogs.ActorUserId`、`Users.Id`、`Users.Username`
- 输出：可空 `nvarchar(128)` 的 `AuditLogs.ActorUsername`，并回填可解析历史数据

- [ ] **步骤 1：创建 migration 前先写一个 SQL Server 测试**

把唯一测试数据库迁移到 `20260726131039_AddImportJobs`，用旧结构有效 SQL 插入一个用户及匹配、孤立、系统三类审计记录，再迁移到最新并断言：

```csharp
Assert.Equal("historical-operator", matched.ActorUsername);
Assert.Equal(matchedActorId, matched.ActorUserId);
Assert.Null(orphan.ActorUsername);
Assert.Equal(orphanActorId, orphan.ActorUserId);
Assert.Null(system.ActorUsername);
Assert.Null(system.ActorUserId);
```

- [ ] **步骤 2：运行 migration 测试并确认 RED**

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~AuditActorUsernameMigrationTests
```

预期：最新 migration 尚未增加及回填 `ActorUsername`，测试失败。

- [ ] **步骤 3：生成 EF migration**

```powershell
dotnet ef migrations add AddAuditActorUsername --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
```

- [ ] **步骤 4：在 `Up` 中加入容错回填**

保留生成的可空列并增加：

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

不得增加外键、默认值、索引或非空约束。`Down` 只删除 `ActorUsername`。

- [ ] **步骤 5：再次运行 migration 测试并确认 GREEN**

预期：测试通过。

### 任务 4：验证完整范围内的变更

**文件：**
- 仅验证；不计划增加源文件

**接口：**
- 输入：上述全部实现与测试
- 输出：新的构建、测试、migration 及差异证据

- [ ] **步骤 1：检查 migration 与模型一致性**

```powershell
dotnet ef migrations has-pending-model-changes --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
```

预期：没有待生成的模型变更。

- [ ] **步骤 2：运行范围内测试和解决方案构建**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AuditWriterTests
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Governance_pages_render_chinese_read_only_and_permission_management_contracts|FullyQualifiedName~AuditActorUsernameMigrationTests"
dotnet build WebPass.sln -c Release --no-restore
```

预期：零失败，构建退出码为 0。

- [ ] **步骤 3：审查范围和生成 SQL**

```powershell
dotnet ef migrations script 20260726131039_AddImportJobs --project src\WebPass.Web\WebPass.Web.csproj --startup-project src\WebPass.Web\WebPass.Web.csproj
git diff --check
git status --short
git diff -- src/WebPass.Web tests/WebPass.UnitTests/Auditing tests/WebPass.IntegrationTests/Presentation tests/WebPass.IntegrationTests/Data docs/superpowers
```

确认脚本增加可空 `nvarchar(128)` 列，只更新匹配用户，不包含审计用户外键，也没有修改无关功能文件。
