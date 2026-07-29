# WebPass 核心平台实施计划

> **面向代理式执行人员：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans，按任务逐项实施本计划。步骤使用复选框（`- [ ]`）语法跟踪。

**目标：** 交付可测试的 WebPass 核心功能：本地登录、逐用户权限、IPv4 CIDR 和服务器清单、Ping、状态更新及审计。

**架构：** ASP.NET Core 10 Razor Pages/MVC 是托管在 IIS 上、使用 SQL Server 2025 Express 的模块化单体。Cookie 身份验证仅含用户标识；授权从数据库加载当前权限。领域服务负责 IP 验证、资产命令、Ping 和审计，页面处理程序保持轻量。

**技术栈：** .NET 10、ASP.NET Core Razor Pages/MVC、EF Core SQL Server、SQL Server 2025 Express、Argon2id、xUnit、Microsoft.AspNetCore.Mvc.Testing。

## 全局约束

- 使用 64 位 IIS 托管的 ASP.NET Core 10 应用程序和 SQL Server 2025 Express。
- 首个版本仅支持 IPv4 CIDR；不依赖外部服务。
- 默认按业务 IP 的数值升序排列服务器。
- Ping 按需执行，绝不自动改变手动状态。
- 使用本地账户；管理员拥有全部功能，普通用户拥有单独存储的权限。
- 审计登录、权限、子网、资产、Ping 和手动状态操作，不记录机密。
- 每项任务遵循 TDD，并以聚焦提交结束。

---

## 文件结构

- `src/WebPass.Web/Domain/`：实体和枚举。
- `src/WebPass.Web/Application/`：契约和用例。
- `src/WebPass.Web/Infrastructure/`：EF Core、Argon2id、Ping、授权和审计。
- `src/WebPass.Web/Pages/`：Razor 页面和处理程序。
- `tests/WebPass.UnitTests/`：领域/服务测试。
- `tests/WebPass.IntegrationTests/`：数据库和 HTTP 测试。

### 任务 1：搭建 Web 应用程序并验证运行时选项

**文件：** 创建 `WebPass.sln`、`src/WebPass.Web/WebPass.Web.csproj`、`Program.cs`、`appsettings.json`、`Configuration/WebPassOptions.cs`、两个测试项目及 `WebPassFactory.cs`。

**接口：** 提供 `WebPassOptions { int PingTimeoutMilliseconds; int PingMaxConcurrency; int PingPerUserPerMinute; }`，并通过 `IValidateOptions<WebPassOptions>` 提供启动验证。

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public void Rejects_non_positive_ping_timeout()
{
    var result = new WebPassOptionsValidator().Validate(null,
        new WebPassOptions { PingTimeoutMilliseconds = 0, PingMaxConcurrency = 2, PingPerUserPerMinute = 5 });
    Assert.False(result.Succeeded);
}
```

- [ ] **步骤 2：运行测试以确认失败**

运行：`dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~WebPassOptionsTests`

预期：失败，因为尚不存在 `WebPassOptionsValidator`。

- [ ] **步骤 3：实现最小配置边界**

```csharp
public sealed class WebPassOptionsValidator : IValidateOptions<WebPassOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPassOptions value) =>
        value.PingTimeoutMilliseconds > 0 && value.PingMaxConcurrency > 0 && value.PingPerUserPerMinute > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Ping values must be positive.");
}
```

注册 `AddOptions<WebPassOptions>().BindConfiguration("WebPass").ValidateOnStart()`、Razor Pages、防伪、Cookie 身份验证、SQL Server DbContext 和两个测试项目。

- [ ] **步骤 4：运行测试并构建**：运行上述测试（预期通过），再运行 `dotnet build WebPass.sln -c Release`（预期通过）。
- [ ] **步骤 5：提交**：`git add WebPass.sln src/WebPass.Web tests`，随后执行 `git commit -m "chore: scaffold WebPass application"`。

### 任务 2：添加 EF Core 实体、迁移、本地身份验证和审计

**文件：** 创建 `AppUser`、`UserPermission`、`Subnet`、`ServerAsset`、`PingResult`、`AuditLog` 实体，`AliveStatus` 枚举、`WebPassDbContext`、初始迁移、Argon2 密码哈希器、登录服务、审计写入器以及登录页面；添加持久化和登录测试。

**接口：** 提供 `Task<LoginResult> LoginAsync(string username, string password, IPAddress sourceIp, CancellationToken ct)`、`Task WriteAsync(AuditEntry entry, CancellationToken ct)`、活动 IP 唯一性及 `ServerAsset` 的 `rowversion` 并发控制。

- [ ] **步骤 1：编写失败测试**：验证重复的活动业务 IP 被拒绝，以及五次失败登录后账户锁定。
- [ ] **步骤 2：运行聚焦测试**：`dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~LoginServiceTests`；预期失败，因为登录服务尚不存在。
- [ ] **步骤 3：实现持久化、哈希、Cookie 登录和审计脱敏**。

```csharp
builder.Entity<ServerAsset>().HasIndex(x => x.BusinessIp).IsUnique().HasFilter("[IsArchived] = 0");
builder.Entity<ServerAsset>().Property(x => x.RowVersion).IsRowVersion();
builder.Entity<UserPermission>().HasIndex(x => new { x.UserId, x.PermissionCode }).IsUnique();
```

为 `ServerAsset` 提供 `BusinessIpNumber: long`、`IsArchived`、时间戳和操作者 ID。使用随机 Argon2id 盐；五次失败后锁定；Cookie 声明仅为 `ClaimTypes.NameIdentifier`。`AuditWriter` 必须拒绝名称包含 `password`、`secret`、`ciphertext`、`token`、`cookie`、`authorization` 或 `key` 的负载属性。

- [ ] **步骤 4：应用迁移并运行测试**：先运行 `dotnet ef database update --project src/WebPass.Web --startup-project src/WebPass.Web`，再运行 LoginService 与 CorePersistence 聚焦测试；均预期通过。
- [ ] **步骤 5：提交**：提交领域、数据、基础设施、登录页面和测试，消息为 `feat: add core data and local authentication`。

### 任务 3：实现逐用户授权和 CIDR 子网管理

**文件：** 创建 `PermissionCode`、授权处理程序、`Ipv4Cidr`、`SubnetService`、子网页面、管理用户页面及 CIDR/授权测试。

**接口：** 提供 `PermissionCode.AssetView, AssetCreate, AssetEdit, AssetArchive, PingExecute, StatusMarkAlive, ImportData, ExportData, SecretReveal, SubnetManage, AuditView`，以及 `Ipv4Cidr.Parse(string)`、`ContainsUsable(IPAddress)`、`GetUsableAddressCount()`、`EnumerateUsableAddresses(int skip, int take)`。

- [ ] **步骤 1：编写失败的授权与 CIDR 测试**：验证 `/24` 具有 254 个可用地址，广播地址不可用；没有 `AssetCreate` 的用户被拒绝。
- [ ] **步骤 2：运行测试确认失败**：运行 `Ipv4CidrTests`；预期失败，因为 CIDR 代码尚不存在。
- [ ] **步骤 3：实现权限处理程序、子网服务和页面**。

```csharp
public sealed record PermissionRequirement(PermissionCode Code) : IAuthorizationRequirement;
public sealed record SubnetInput(string Name, string Cidr, string Location, string? Notes, bool IsEnabled);
```

管理员绕过单独权限行；每个请求均从 `UserPermissions` 检查普通用户。拒绝 IPv6、格式错误 CIDR、重叠范围、网络/广播地址，以及删除仍有活动资产的子网。管理页面只能授予已批准的普通用户权限，且必须拒绝编辑管理员授权。

- [ ] **步骤 4：运行测试**：运行 `Ipv4CidrTests` 和 `PermissionTests`；预期管理员绕过通过、普通用户拒绝。
- [ ] **步骤 5：提交**：提交应用、授权基础设施、子网页面、管理页面和测试，消息为 `feat: add permissions and subnet management`。

### 任务 4：实现资产 CRUD、动态地址池、Ping 和核心 UI

**文件：** 创建 `ServerAssetInput`、`ServerAssetService`、`PingService`、`SystemPingTransport`、服务器页面、审计页面及资产/Ping 集成测试。

**接口：** 提供 `CreateAsync(ServerAssetInput, Guid, CancellationToken)`、`UpdateAsync(Guid, ServerAssetInput, byte[], Guid, CancellationToken)`、`ListAsync(ServerListQuery, CancellationToken)`、`ExecuteAsync(Guid assetId, Guid actorUserId, CancellationToken)` 和 `MarkAliveAsync(Guid assetId, Guid actorUserId, byte[] rowVersion, CancellationToken)`。

- [ ] **步骤 1：编写失败的资产、排序和 Ping 测试**：验证 `10.0.0.9` 排在 `10.0.0.10` 前，且 Ping 成功不改变手动状态。
- [ ] **步骤 2：运行测试确认失败**：运行 `AssetAndPingTests`；预期失败，因为资产和 Ping 服务尚不存在。
- [ ] **步骤 3：实现服务和 Razor 处理程序**。

```csharp
public sealed record ServerAssetInput(string BusinessIp, string Location, AliveStatus AliveStatus,
    string ComputerName, string SystemName, string? OperatingSystemVersion,
    string? DatabaseVersion, string? Notes);
```

将 IPv4 转换为无符号 32 位 `long`，要求存在已启用且包含该地址的子网，使用活动 IP 唯一性，并审计每个命令。列表默认 `OrderBy(x => x.BusinessIpNumber)`；完整地址池视图调用 `EnumerateUsableAddresses(skip, take)` 并覆盖已登记资产，而不是创建空数据库行。Ping 通过接口使用 `System.Net.NetworkInformation.Ping`，具备固定超时、`SemaphoreSlim` 全局限制、逐用户速率限制、允许子网检查、结果持久化和审计。只有单独的 `MarkAliveAsync` 端点更新状态。

- [ ] **步骤 4：运行测试和应用冒烟测试**：运行 `dotnet test WebPass.sln -c Release`（预期通过）及 `dotnet run --project src/WebPass.Web`（预期启动并提供 `/login`、`/servers`、`/subnets`、`/audit`、`/admin/users`）。
- [ ] **步骤 5：提交**：提交资产、Ping、网络基础设施、服务器/审计页面和测试，消息为 `feat: add audited server inventory and ping`。

## 自查

- 覆盖范围：四项任务实现完整核心阶段：本地身份验证、用户级权限、审计、IPv4 CIDR、动态地址池、资产 CRUD、数值排序、Ping 和独立手动状态。
- 计划边界：密码加密/显示、文件导入/导出、加密备份及 IIS 生产运行手册由安全数据计划实施。
- 一致性：`PermissionCode`、`Ipv4Cidr`、`ServerAssetInput`、`ServerAssetService` 和 `PingService` 均在使用前定义。
