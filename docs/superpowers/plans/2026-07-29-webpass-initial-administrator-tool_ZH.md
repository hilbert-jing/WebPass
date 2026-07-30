# WebPass 初始管理员工具实施计划

> **面向代理式执行人员：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans，按任务逐项实施本计划。步骤使用复选框（`- [ ]`）语法跟踪。

**目标：** 新增一个可独立发布的本地控制台工具，使用现有 SQL Server 模型和 Argon2id 密码哈希器创建已启用的 WebPass 管理员。

**架构：** 新建 `.NET 10` 控制台项目引用 `WebPass.Web`，包含可测试的 `AdministratorInitializer` 和轻量命令行应用。它接收连接字符串和用户名，不回显地读取两次密码，写入一条 `AppUser`，且绝不在 Web 应用程序内运行。

**技术栈：** .NET 10、C#、EF Core 10 SQL Server 与 InMemory 提供程序、现有 WebPass Argon2id 实现、xUnit。

## 全局约束

- 不检查 `Users` 是否为空，也不检查是否已有其他管理员。
- 每次成功调用都创建一个具有不同用户名的管理员。
- 不新增 Web 页面、HTTP 终结点、启动钩子、重置流程、用户管理功能或管理员数量限制。
- 复用 `WebPassDbContext`、`AppUser`、`IPasswordHasher` 和 `Argon2PasswordHasher`；不得复制或替换密码哈希逻辑。
- 除 `AppUser.PasswordHash` 外，绝不打印或持久化明文密码。
- 不新增 `UserPermission` 行，不改变管理员授权，不新增审计事件，也不修改身份验证、授权、Cookie、会话或审计代码。
- 不改变 EF Core 模型，也不新增迁移。
- 行为变更采用 TDD，每个任务以聚焦提交结束。

## 文件结构

- `src/WebPass.AdminInit/WebPass.AdminInit.csproj`：可独立发布、引用 `WebPass.Web` 的控制台项目。
- `src/WebPass.AdminInit/AdministratorInitializer.cs`：验证、重复检测、哈希及单次用户插入。
- `src/WebPass.AdminInit/Program.cs`：参数解析、隐藏密码输入、退出代码映射、SQL Server 上下文构造和无机密控制台输出。
- `tests/WebPass.UnitTests/AdminInit/AdministratorInitializerTests.cs`：初始化器行为测试。
- `tests/WebPass.UnitTests/AdminInit/AdminInitApplicationTests.cs`：命令契约、提示流程、输出脱敏和退出代码测试。
- `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`：引用控制台项目。
- `WebPass.sln`：包含控制台项目。
- `docs/deployment/windows-server-iis.md`：发布和本地执行说明。

### 任务 1：添加管理员初始化服务

**文件：** 创建控制台项目、`AdministratorInitializer.cs` 与初始化器测试；修改单元测试项目和解决方案。

**接口：**

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

- [ ] **步骤 1：添加项目骨架和测试引用。** 控制台项目目标框架为 `net10.0`，启用可空引用和隐式 using，引用 `..\WebPass.Web\WebPass.Web.csproj`。将项目添加到解决方案：

```powershell
dotnet sln WebPass.sln add src\WebPass.AdminInit\WebPass.AdminInit.csproj
```

并在 `WebPass.UnitTests.csproj` 添加对该项目的引用。

- [ ] **步骤 2：编写失败的初始化器测试。** 验证创建的管理员用户名经过修剪、已启用、`IsAdministrator = true`、`MustChangePassword = false`、失败次数为零、未锁定，密码可由真实 `Argon2PasswordHasher` 验证，且没有权限行。还要验证已有普通用户或管理员不阻止创建；重复用户名、空白/超长用户名、空密码及确认不匹配均不写入数据库。

- [ ] **步骤 3：运行测试确认 RED。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdministratorInitializerTests
```

预期：因相关类型尚不存在而编译失败。

- [ ] **步骤 4：实现最小初始化器。** 修剪并验证用户名（非空、最长 128 字符）；拒绝空白密码和确认不一致；只检查规范化用户名是否存在。使用既有哈希器生成密码哈希，创建一个启用的管理员并调用一次 `SaveChangesAsync`。捕获 SQL Server 唯一索引错误 `2601` 或 `2627`，将其映射为 `DuplicateUsername`；其余数据库错误继续抛出。

- [ ] **步骤 5：运行 GREEN 和身份回归测试。** 初始化器测试及现有身份测试必须全部通过，Argon2id 和登录行为不变。

- [ ] **步骤 6：提交任务 1。**

```powershell
git add WebPass.sln src\WebPass.AdminInit tests\WebPass.UnitTests
git commit -m "feat: add administrator initialization service"
```

### 任务 2：添加交互式控制台命令

**文件：** 修改 `WebPass.AdminInit.csproj` 和 `Program.cs`；创建 `AdminInitApplicationTests.cs`。

**接口：**

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

退出代码：`0` 已创建，`1` 运行故障，`2` 无效用法或输入，`3` 重复用户名。

- [ ] **步骤 1：编写失败的命令测试。** 使用不回显输入的伪控制台，验证有效命令创建用户并返回 `0`；缺失、未知、重复或无值参数返回 `2` 且不打开数据库；交互式输入不可用返回 `2`；确认密码不匹配不写库；重复用户名返回 `3`；数据库故障返回 `1`。任何输出都不得包含密码或连接字符串。

- [ ] **步骤 2：运行命令测试确认 RED。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInitApplicationTests
```

- [ ] **步骤 3：实现参数解析和退出代码映射。** 项目输出类型设为 `Exe`。严格要求且只允许一次 `--connection-string <value>` 和 `--username <value>`；无效用法输出：

```text
Usage: WebPass.AdminInit --connection-string <value> --username <value>
```

先验证参数和用户名，再读取两次密码。提示为 `Password: ` 和 `Confirm password: `；无法交互输入时输出 `Interactive password input is required.`。根据初始化结果输出稳定、无机密消息：成功时 `Administrator '<username>' created.`，重复时 `Username already exists.`。数据库或意外运行错误只输出 `Administrator creation failed. Verify database connectivity and permissions.`。

`SystemAdminInitConsole.ReadSecret` 必须拒绝 `Console.IsInputRedirected`，通过 `Console.ReadKey(intercept: true)` 逐字符读取，支持退格和 Enter，不回显字符。`Main` 使用 `UseSqlServer(connectionString)` 创建 `WebPassDbContext` 后调用 `RunAsync`。

- [ ] **步骤 4：运行命令测试确认 GREEN。** 成功、无效参数、确认不匹配、重复用户名、脱敏输出与退出代码测试均通过。
- [ ] **步骤 5：运行全部管理员初始化测试。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

- [ ] **步骤 6：提交任务 2。**

```powershell
git add src\WebPass.AdminInit\Program.cs tests\WebPass.UnitTests\AdminInit
git commit -m "feat: add administrator initialization command"
```

### 任务 3：记录、发布并验证该工具

**文件：** 修改 `docs/deployment/windows-server-iis.md`。

- [ ] **步骤 1：添加部署运行手册章节。** 在生产检查前加入“创建管理员”章节，包含独立发布命令：

```powershell
dotnet publish src\WebPass.AdminInit -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\AdminInit
```

以及本机执行示例：

```powershell
C:\WebPass\AdminInit\WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

说明密码在隐藏提示中输入和确认；工具不会检查是否已有用户或管理员，每次成功调用为不同用户名创建另一位管理员；运行网站不依赖此工具，操作人员不再需要时可删除 `C:\WebPass\AdminInit`。

- [ ] **步骤 2：运行聚焦测试。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

- [ ] **步骤 3：发布工具。**

```powershell
dotnet publish src\WebPass.AdminInit\WebPass.AdminInit.csproj -c Release -r win-x64 --self-contained false
```

预期：通过，并存在 `src/WebPass.AdminInit/bin/Release/net10.0/win-x64/publish/WebPass.AdminInit.exe`。

- [ ] **步骤 4：验证无需 EF 迁移。**

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release --no-build
```

预期：`No changes have been made to the model since the last migration.`

- [ ] **步骤 5：运行完整 Release 测试套件。**

```powershell
dotnet test WebPass.sln -c Release
```

预期：单元和集成测试均零失败、零跳过。

- [ ] **步骤 6：检查最终差异。**

```powershell
git diff --check
git status --short
```

预期：无空白错误；仅修改获批准的管理员工具、解决方案、测试项目、测试及部署文档。

- [ ] **步骤 7：提交任务 3。**

```powershell
git add docs\deployment\windows-server-iis.md
git commit -m "docs: add administrator initialization instructions"
```

## 最终审查清单

- [ ] 工具不检查用户或管理员总数。
- [ ] 已有普通用户和管理员不会阻止创建。
- [ ] 重复用户名被拒绝，且不会产生另一条记录。
- [ ] 密码输入隐藏、经确认、使用 Argon2id 哈希，且绝不打印。
- [ ] 创建的用户是启用的管理员，且没有 `UserPermission` 行。
- [ ] Web 应用程序没有初始化器终结点或启动行为。
- [ ] 身份验证、授权、Cookie、会话和审计代码未改变。
- [ ] EF 报告无待处理模型更改。
- [ ] 工具可为 `win-x64` 独立发布。
- [ ] 完整 Release 测试套件通过。
