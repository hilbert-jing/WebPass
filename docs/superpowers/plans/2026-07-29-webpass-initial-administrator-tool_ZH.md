# WebPass 初始管理员工具实施计划

> **面向代理式执行人员：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans，按任务逐项实施本计划。使用复选框（`- [ ]`）语法跟踪步骤。

**目标：** 新增一个单独发布的本地控制台工具，使用现有 SQL Server 模型和 Argon2id 密码哈希器创建已启用的 WebPass 管理员。

**架构：** 新建 `.NET 10` 控制台项目引用 `WebPass.Web`，包含可测试的 `AdministratorInitializer` 和轻量命令行应用。该工具接收连接字符串和用户名，以不回显方式读取两次密码，写入一条 `AppUser`，且绝不在 Web 应用程序内运行。

**技术栈：** .NET 10、C#、EF Core 10 SQL Server 和 InMemory 提供程序、现有 WebPass Argon2id 实现、xUnit。

## 全局约束

- 不检查 `Users` 是否为空。
- 不检查是否已有其他管理员。
- 每次成功调用均创建一个具有不同用户名的管理员。
- 不新增 Web 页面、HTTP 终结点、启动钩子、重置流程、用户管理功能或管理员数量限制。
- 复用 `WebPassDbContext`、`AppUser`、`IPasswordHasher` 和 `Argon2PasswordHasher`；不得复制或替换密码哈希逻辑。
- 除 `AppUser.PasswordHash` 外，绝不打印或持久化明文密码。
- 不新增 `UserPermission` 行，也不改变管理员授权。
- 不新增审计事件，也不修改现有身份验证、授权、Cookie、会话或审计代码。
- 不改变 EF Core 模型，也不新增迁移。
- 行为变更采用 TDD，每个任务以聚焦提交结束。

---

## 文件结构

- `src/WebPass.AdminInit/WebPass.AdminInit.csproj`：可独立发布、引用 `WebPass.Web` 的控制台项目。
- `src/WebPass.AdminInit/AdministratorInitializer.cs`：验证、重复检测、哈希和单次用户插入。
- `src/WebPass.AdminInit/Program.cs`：参数解析、隐藏密码输入、退出代码映射、SQL Server 上下文构造和无机密控制台输出。
- `tests/WebPass.UnitTests/AdminInit/AdministratorInitializerTests.cs`：针对 EF Core InMemory 数据库及真实 Argon2id 哈希器的初始化器行为测试。
- `tests/WebPass.UnitTests/AdminInit/AdminInitApplicationTests.cs`：命令契约、提示流程、输出脱敏和退出代码测试。
- `tests/WebPass.UnitTests/WebPass.UnitTests.csproj`：引用控制台项目。
- `WebPass.sln`：包含控制台项目。
- `docs/deployment/windows-server-iis.md`：发布和本地执行说明。

### 任务 1：添加管理员初始化服务

**文件：** 创建 `src/WebPass.AdminInit/WebPass.AdminInit.csproj`、`AdministratorInitializer.cs`、`AdministratorInitializerTests.cs`；修改 `WebPass.UnitTests.csproj` 和 `WebPass.sln`。

**接口：** 消费 `WebPassDbContext`、`AppUser` 和 `IPasswordHasher`；提供如下结果类型和创建方法：

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

- [ ] **步骤 1：添加项目骨架和测试引用。** 创建控制台项目，目标框架为 `net10.0`，启用可空引用和隐式 using，并引用 `..\WebPass.Web\WebPass.Web.csproj`。运行：

```powershell
dotnet sln WebPass.sln add src\WebPass.AdminInit\WebPass.AdminInit.csproj
```

在 `tests/WebPass.UnitTests/WebPass.UnitTests.csproj` 添加：

```xml
<ProjectReference Include="..\..\src\WebPass.AdminInit\WebPass.AdminInit.csproj" />
```

- [ ] **步骤 2：编写失败的初始化器测试。** 测试必须验证：修剪用户名后创建启用的管理员；密码可由真实 `Argon2PasswordHasher` 验证；`MustChangePassword = false`、`FailedLoginCount = 0`、`LockedUntil = null`，且没有权限行。另需验证已有普通用户或管理员不阻止创建、重复用户名不产生第二条记录、空白/超长用户名被拒绝、空密码或确认不匹配在写库前被拒绝。使用 `UseInMemoryDatabase(Guid.NewGuid().ToString("N"))` 创建测试 `WebPassDbContext`。

- [ ] **步骤 3：运行测试确认 RED。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdministratorInitializerTests
```

预期：编译失败，因为尚不存在 `AdministratorInitializer`、`AdministratorInitializationResult` 和 `AdministratorInitializationResultKind`。

- [ ] **步骤 4：实现最小初始化器。** 规范化用户名（修剪、非空、最多 128 字符）；拒绝空白密码及确认不一致；只检查规范化用户名是否存在。通过现有哈希器生成 `PasswordHash`，插入一个启用的管理员并只调用一次 `SaveChangesAsync`。捕获 SQL Server 唯一索引错误 `2601` 或 `2627`，分离新增实体后返回 `DuplicateUsername`；其他异常不吞掉。

- [ ] **步骤 5：运行初始化器测试确认 GREEN。** 重复步骤 3 的命令；预期管理员创建、已有用户、重复用户名、用户名验证和密码验证均通过。
- [ ] **步骤 6：运行现有身份测试。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter "FullyQualifiedName~Identity|FullyQualifiedName~AdministratorInitializerTests"
```

预期：通过，Argon2id 和登录行为未发生变化。

- [ ] **步骤 7：提交任务 1。**

```powershell
git add WebPass.sln src\WebPass.AdminInit tests\WebPass.UnitTests
git commit -m "feat: add administrator initialization service"
```

### 任务 2：添加交互式控制台命令

**文件：** 修改 `WebPass.AdminInit.csproj` 和 `Program.cs`；创建 `AdminInitApplicationTests.cs`。

**接口：** `IAdminInitConsole` 提供 `ReadSecret(string prompt)` 与 `WriteLine(string message)`；`AdminInitApplication.RunAsync(string[] args, IAdminInitConsole console, Func<string, WebPassDbContext> dbFactory, CancellationToken ct)` 返回进程退出代码。退出代码为：`0` 已创建、`1` 运行故障、`2` 无效用法/输入、`3` 重复用户名。

- [ ] **步骤 1：编写失败的命令测试。** 使用不会回显输入、仅存储提示和输出的伪控制台。测试有效命令创建用户并返回 `0`；参数缺失、未知、重复或无值时返回 `2` 且不打开数据库；交互式输入不可用时返回 `2`；确认密码不匹配时返回 `2` 且不写库；重复用户名返回 `3`；数据库构造或操作失败时返回 `1`。所有输出均不得包含密码或连接字符串。

- [ ] **步骤 2：运行命令测试确认 RED。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInitApplicationTests
```

预期：失败，因为 `IAdminInitConsole` 和 `AdminInitApplication` 尚不存在。

- [ ] **步骤 3：实现参数解析和退出代码映射。** 在项目文件加入：

```xml
<OutputType>Exe</OutputType>
```

`Program.cs` 应严格解析恰好四个参数，只接受且只接受一次 `--connection-string <value>` 和 `--username <value>`；无效用法输出：

```text
Usage: WebPass.AdminInit --connection-string <value> --username <value>
```

用户名在开库前修剪并验证为 1 至 128 个字符。提示应为 `Password: ` 与 `Confirm password: `；输入不可用时输出 `Interactive password input is required.` 并返回 `2`。调用 `AdministratorInitializer` 后，将成功映射为 `Administrator '<username>' created.`，重复映射为 `Username already exists.`，无效用户名、空密码和不匹配确认均映射为稳定且不含机密的消息。运行错误只能输出：`Administrator creation failed. Verify database connectivity and permissions.`。

`SystemAdminInitConsole.ReadSecret` 必须拒绝 `Console.IsInputRedirected`，使用 `Console.ReadKey(intercept: true)` 逐字符读取，支持退格和 Enter，但不得回显字符。`Main` 使用 `UseSqlServer(connectionString)` 构造 `WebPassDbContext`，再调用 `RunAsync`。

- [ ] **步骤 4：运行命令测试确认 GREEN。** 重复步骤 2 的命令；预期成功创建、无效参数、确认不匹配、重复用户名、脱敏输出和稳定退出代码均通过。
- [ ] **步骤 5：运行全部管理员初始化测试。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

预期：通过。

- [ ] **步骤 6：提交任务 2。**

```powershell
git add src\WebPass.AdminInit\Program.cs tests\WebPass.UnitTests\AdminInit
git commit -m "feat: add administrator initialization command"
```

### 任务 3：记录、发布并验证该工具

**文件：** 修改 `docs/deployment/windows-server-iis.md`。

**接口：** 使用任务 1–2 产生的 `WebPass.AdminInit.exe`，并在部署运行手册中提供精确发布和执行命令。

- [ ] **步骤 1：添加部署运行手册章节。** 在 `docs/deployment/windows-server-iis.md` 的生产检查之前插入“创建管理员”章节：

````markdown
## 创建管理员

单独发布本地初始化工具：

```powershell
dotnet publish src\WebPass.AdminInit -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\AdminInit
```

使用可向 WebPass 数据库插入记录的部署身份，在本机运行：

```powershell
C:\WebPass\AdminInit\WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

在隐藏提示中输入并确认密码。该命令不会检查是否已有用户或管理员；每次成功调用都会为请求的不同用户名创建另一位管理员。

运行中的网站不需要此工具。若操作人员不需要保留它，可在使用后删除 `C:\WebPass\AdminInit`。
````

重新编号后续运行手册章节，使标题保持连续。

- [ ] **步骤 2：运行聚焦测试。**

```powershell
dotnet test tests\WebPass.UnitTests\WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~AdminInit
```

预期：通过。

- [ ] **步骤 3：发布工具。**

```powershell
dotnet publish src\WebPass.AdminInit\WebPass.AdminInit.csproj -c Release -r win-x64 --self-contained false
```

预期：通过，并存在 `src/WebPass.AdminInit/bin/Release/net10.0/win-x64/publish/WebPass.AdminInit.exe`。

- [ ] **步骤 4：验证无需 EF 迁移。** 使用仓库本地 EF 工具运行：

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

预期：全部单元和集成测试通过，零失败、零跳过。

- [ ] **步骤 6：检查最终差异。**

```powershell
git diff --check
git status --short
```

预期：无空白错误；仅修改已批准的管理员工具、解决方案、测试项目、测试和部署文档文件。

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
