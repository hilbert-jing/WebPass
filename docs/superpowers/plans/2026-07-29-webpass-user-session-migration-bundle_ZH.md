# WebPass 用户管理、会话期限与 Migration Bundle 实施计划

> **面向代理式执行人员：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans，按任务逐项实施本计划。步骤使用复选框（`- [ ]`）语法跟踪。

**目标：** 在既有 WebPass 核心平台和安全数据功能上，增加普通用户创建与密码重置、30 分钟 Cookie 空闲期限和 8 小时绝对会话上限、显式退出，以及可重复执行的 Windows x64 EF Core migration bundle 构建和部署流程。

**架构：** 沿用现有本地认证、Argon2id、逐用户权限、审计、Cookie 安全属性及 EF Core 模型。不引入 ASP.NET Core Identity、数据库会话表或新的 EF 迁移。用户管理保留在现有管理员 Razor Page；会话配置位于 `Program.cs`；bundle 由 PowerShell 脚本生成，运行中的网站不自动执行迁移。

**技术栈：** .NET 10、ASP.NET Core Razor Pages、EF Core SQL Server、SQL Server 2025 Express、Argon2id、PowerShell、EF Core migration bundle、xUnit。

## 全局约束

- 创建普通用户时不检查 `Users` 是否为空，也不限制用户或管理员总数。
- 新普通用户默认启用、非管理员、没有 `UserPermission`，初始密码固定为 `abc123`。
- 管理员重置普通用户密码同样设为 `abc123`；不要求首次登录改密，`MustChangePassword` 保持 `false`。
- 重置密码不撤销目标用户已签发的 Cookie。
- 不新增实体、表、列、索引、模型快照或迁移。
- 不记录默认密码、密码哈希、Cookie、Claim 或其他凭据到审计、日志或错误中。
- 生成的 `WebPass.Migrations.exe` 是部署产物，不提交至 Git；每个审核发布版本必须重新生成。
- 每项任务遵循 TDD，并在审核检查点后继续后续任务。

---

## 文件结构

- `src/WebPass.Web/Pages/Admin/Users.cshtml` 与 `.cs`：普通用户创建、密码重置、并发处理和审计。
- `src/WebPass.Web/Infrastructure/Identity/LoginService.cs`：在登录票据中写入原始登录时间 Claim。
- `src/WebPass.Web/Program.cs`：Cookie 空闲期限、滑动续期、绝对上限验证和退出路由。
- `src/WebPass.Web/Pages/Logout.cshtml` 与 `.cs`：仅 POST 的显式退出。
- `scripts/Build-WebPassMigrationBundle.ps1`：可重复运行的 bundle 构建脚本。
- `docs/deployment/windows-server-iis.md`：bundle 部署说明。
- `tests/WebPass.IntegrationTests`：管理员、会话、退出和 bundle 测试。

### 任务 1：创建普通用户和重置普通用户密码

**范围：** 在受管理员策略保护的 `/admin/users` 页面新增创建表单及密码重置操作；页面模型新增 `IPasswordHasher` 依赖，复用已有 `WebPassDbContext`、`PermissionAuthorizationHandler`、`AuditWriter`、授权、事务及并发模式。

- [ ] **步骤 1：编写失败的集成测试。** 覆盖以下行为：管理员可创建用户名经过修剪的普通用户；创建的用户具有 Argon2id 哈希的 `abc123`、`IsAdministrator = false`、`IsEnabled = true`、`MustChangePassword = false`、零失败次数、无锁定时间和零权限。已有普通用户或管理员不得阻止创建。空白、超长或重复用户名不得创建行。非管理员不得调用创建或重置处理程序。成功创建与重置必须写入无机密审计。

- [ ] **步骤 2：运行聚焦测试确认 RED。**

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~AdminUsersTests
```

预期：因创建/重置处理程序及 UI 尚不存在而失败。

- [ ] **步骤 3：实现创建用户。** 在页面顶端增加仅含用户名和 “Create user” 按钮的防伪 POST 表单，并实现 `OnPostCreateAsync`。先调用 `EnsureAdministratorAsync`；用户名修剪后要求长度 1–128，预检重复用户名，再对 `abc123` 使用 `IPasswordHasher` 生成新的 Argon2id 哈希。创建启用的非管理员 `AppUser`，不添加权限行。用户写入与成功 `UserCreate` 审计在同一关系型事务中。除预检外，捕获 SQL Server 唯一约束错误 `2601/2627`，防止并发创建同名用户。审计仅可包含用户名和对象标识等非敏感信息。

- [ ] **步骤 4：实现密码重置。** 每个非管理员用户行显示 “Reset password” POST 表单，并提交 `userId` 及 Base64 `rowVersion`；管理员行不得显示该按钮。`OnPostResetPasswordAsync` 必须重新确认当前操作者为管理员，加载目标用户，拒绝管理员目标，将提交的版本设为 EF 原始并发值，把密码替换为 `abc123` 的新 Argon2id 哈希，设置 `MustChangePassword = false`，清除失败次数和锁定时间。用户更新和 `UserPasswordReset` 成功审计位于同一事务。并发冲突返回 HTTP 409；目标不存在使用既有未找到路径。重置不得变更权限、启用状态或管理员标记。

- [ ] **步骤 5：运行 GREEN 及回归测试。** 重复管理员聚焦测试，并运行身份验证、授权和审计相关测试；预期零失败、零跳过。

- [ ] **步骤 6：审查并提交。**

```powershell
git diff --check
git add src/WebPass.Web/Pages/Admin tests/WebPass.IntegrationTests
git commit -m "feat: add ordinary user creation and password reset"
```

在进入任务 2 前停止并取得批准。

### 任务 2：实施会话期限和显式退出

**范围：** 保持现有 `Secure`、`HttpOnly`、`SameSite=Strict`、登录页和拒绝访问行为，增加 Cookie 30 分钟空闲期限、8 小时绝对会话上限和 `/logout` POST 终结点。

- [ ] **步骤 1：编写失败的认证与退出测试。** 验证 Cookie 配置 `ExpireTimeSpan = TimeSpan.FromMinutes(30)` 与 `SlidingExpiration = true`；成功登录票据包含原始登录 UTC 时间的 WebPass 私有 Unix 秒 Claim；8 小时以内有效，达到或超过 8 小时无效；缺失、损坏或未来时间 Claim 均被拒绝；POST 退出清除认证状态并写入无机密 `Logout` 审计。

- [ ] **步骤 2：运行测试确认 RED。**

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter "FullyQualifiedName~AuthenticationSessionTests|FullyQualifiedName~Logout"
```

- [ ] **步骤 3：实现登录时间 Claim 与 Cookie 策略。** 登录成功时，在受保护的认证票据内加入私有 Claim，值为原始登录 UTC 时间的 Unix 秒数。于 `Program.cs` 设置 30 分钟过期和滑动续期；续期绝不可重写该原始时间。在 Cookie `OnValidatePrincipal` 中读取并验证该 Claim，缺失、格式错误、未来时间或已达 8 小时均拒绝票据并删除 Cookie。部署该版本后，旧票据会因缺少 Claim 失效一次。

- [ ] **步骤 4：实现退出。** 新增 `/logout` Razor Page，只接受已认证用户的 POST。它从 Claim 读取当前用户 ID，写入不含 Cookie、Claim 内容或其他机密的 `Logout` 成功审计，调用 Cookie `SignOutAsync`，然后跳转 `/login`。共享导航对已登录用户显示 POST 退出表单，对未登录用户保留登录链接。默认 Razor Pages 防伪机制保护退出请求。

- [ ] **步骤 5：运行 GREEN 及回归测试。** 运行聚焦会话/退出测试，以及身份、重新验证和安全测试；预期所有测试通过。

- [ ] **步骤 6：审查并提交。**

```powershell
git diff --check
git add src/WebPass.Web/Program.cs src/WebPass.Web/Infrastructure/Identity `
  src/WebPass.Web/Pages tests/WebPass.IntegrationTests
git commit -m "feat: enforce session limits and add logout"
```

在进入任务 3 前停止并取得批准。

### 任务 3：构建、部署并验证 Migration Bundle

**范围：** 将直接 `dotnet ef database update` 部署替换为受版本约束、可重复的 Windows x64 framework-dependent migration bundle。

- [ ] **步骤 1：编写失败的 bundle 集成测试。** 测试在唯一 SQL Server 测试数据库与临时输出目录中执行构建脚本，确认产生 `WebPass.Migrations.exe`，使用连接字符串运行 bundle，验证所有已提交迁移已应用，并清理数据库和临时目录。

- [ ] **步骤 2：运行测试确认 RED。**

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release --filter FullyQualifiedName~MigrationBundleTests
```

- [ ] **步骤 3：创建构建脚本。** 添加 `scripts/Build-WebPassMigrationBundle.ps1`。脚本需启用严格模式并在错误时停止；从脚本目录确定仓库根目录；验证 `.tools/dotnet-ef.exe`、Web 项目和启动项目存在；接受可选 `-OutputPath`，默认输出：

```text
src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe
```

创建输出目录并调用 repository-local EF 工具的 `migrations bundle`，使用 `--configuration Release`、`--target-runtime win-x64`、`--force`、正确项目与启动项目以及目标输出路径。不得使用 `--self-contained`，因为部署已需要 .NET 10 Hosting Bundle/runtime。EF 返回非零或产物不存在时必须抛出错误。默认输出位于 `**/bin/` 忽略规则下；自定义输出也只是部署产物，`.exe` 不得提交。

- [ ] **步骤 4：替换直接 EF 部署流程。** 在 `docs/deployment/windows-server-iis.md` 的网站发布后，替换直接的 `dotnet ef database update`：

````markdown
从同一审核源提交构建 migration bundle，并放入暂存目录：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

使用可修改 WebPass 数据库的部署身份应用迁移：

```powershell
C:\WebPass\staging\WebPass.Migrations.exe `
  --connection "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True"
```

bundle 创建或执行失败时停止部署。每个审核发布版本都必须生成新的 bundle；不得复用另一源版本的 bundle。运行中的 WebPass 网站不会自动应用迁移。
````

保留迁移后从运行时 IIS 身份移除高权限数据库权限的现有说明。

- [ ] **步骤 5：运行 bundle 测试确认 GREEN。** 预期构建 framework-dependent `win-x64` 可执行文件，对唯一 SQL Server 数据库应用所有提交迁移，并清理数据库和临时目录。

- [ ] **步骤 6：验证默认输出被忽略且可重复。**

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

预期：两次构建均成功，文件存在，`git check-ignore` 报告 `**/bin/` 规则，生成的可执行文件不出现在 Git 状态中。

- [ ] **步骤 7：审查并提交。**

```powershell
git diff --check
git add -- scripts/Build-WebPassMigrationBundle.ps1 `
  docs/deployment/windows-server-iis.md `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: add repeatable migration bundle deployment"
```

在进入任务 4 前停止并取得批准。

### 任务 4：最终回归和部署验证

**范围：** 不计划修改源文件；若发现已验证的缺陷，返回所属任务并重复其 RED/GREEN 周期。

- [ ] **步骤 1：验证管理员行为。** 运行 `AdminUsersTests`，确认创建、重置、权限、启用状态、审计和并发测试零失败、零跳过。
- [ ] **步骤 2：验证认证行为。** 运行 `AuthenticationSessionTests`、安全、身份和重新验证测试，确认 Cookie 配置、绝对期限、登录、退出、锁定、Argon2id 和重新验证均通过。
- [ ] **步骤 3：验证 bundle 与 EF 模型稳定性。** 运行 `MigrationBundleTests` 以及：

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release --no-build
```

预期：bundle 测试通过，EF 输出 `No changes have been made to the model since the last migration.`。

- [ ] **步骤 4：运行完整 Release 解决方案。**

```powershell
dotnet test WebPass.sln -c Release
```

预期：单元和集成项目零失败、零跳过；记录各项目及总计数量。

- [ ] **步骤 5：按设计审计实现。**

```powershell
git diff --check
git status --short
git log --oneline -4
git diff --name-only ccbdea3
```

确认：设计提交 `ccbdea3` 后仅本计划列出的文件变更；未改实体、`WebPassDbContext`、快照或迁移；未跟踪生成的 `.exe`；不存在用户/管理员总数检查；审计负载构造中未出现默认密码或哈希；现有认证、授权、Cookie 安全标志、锁定、Argon2id 和权限行为均有通过测试覆盖。

- [ ] **步骤 6：完成分支。** 在作出完成声明前使用 `superpowers:verification-before-completion`，随后使用 `superpowers:finishing-a-development-branch` 检测基础分支并提供本地合并、推送/PR 或保持原状选项。未经用户明确选择，不得合并、推送、删除分支或移除工作树。

## 预期提交顺序

1. `feat: add ordinary user creation and password reset`
2. `feat: enforce session limits and add logout`
3. `feat: add repeatable migration bundle deployment`

设计和计划提交先于以上实施提交，且保持分离。
