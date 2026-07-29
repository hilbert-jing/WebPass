# WebPass 用户管理、会话期限与 Migration Bundle 设计

日期：2026-07-29

## 1. 目标与范围

本次在现有 WebPass Core Platform 和 Secure Data 实现上补齐以下能力：

1. 提供可重复执行的 PowerShell 脚本，生成 Windows x64 的 EF Core
   migration bundle：`WebPass.Migrations.exe`。
2. 在现有管理员用户页面创建普通用户，并将普通用户密码重置为固定默认密码
   `abc123`。
3. 实现认证 Cookie 的 30 分钟空闲期限、单次登录 8 小时绝对上限和显式退出。

系统仍是少量内部人员使用的内网工具。本次复用现有本地认证、Argon2id、
逐用户权限、审计、Cookie 安全属性和 EF Core 数据模型，不引入 ASP.NET
Core Identity，不扩展为完整账号生命周期平台。

## 2. 已确认的产品决策

- 创建普通用户时只填写用户名。
- 新用户默认启用、不是管理员、没有任何 `UserPermission`。
- 新用户初始密码固定为 `abc123`。
- 管理员重置普通用户密码时，密码重置为 `abc123`。
- 创建和重置后均不要求用户首次登录修改密码，
  `MustChangePassword` 保持 `false`。
- 密码重置不撤销目标用户已经签发的 Cookie。
- migration bundle 由仓库脚本生成，生成的 `.exe` 不提交到 Git。
- 用户创建和密码重置直接实现在现有管理员 Razor Page 中；会话配置和
  8 小时验证直接放在 `Program.cs` 的 Cookie 配置中。

## 3. 现有能力与依赖

### 3.1 用户与密码

现有 `AppUser` 已包含本次所需字段：

- `Username`
- `PasswordHash`
- `IsAdministrator`
- `IsEnabled`
- `FailedLoginCount`
- `LockedUntil`
- `MustChangePassword`
- `RowVersion`
- `Permissions`

`WebPassDbContext` 已对 `Username` 建立唯一索引，对 `RowVersion` 配置并发
标记。现有 `IPasswordHasher` 和 `Argon2PasswordHasher` 提供 Argon2id
哈希和验证。因此本次不修改实体、模型快照或数据库迁移。

### 3.2 管理员用户页面

`Pages/Admin/Users` 已受管理员策略保护，并已提供普通用户启用/禁用和权限
替换。页面模型已依赖：

- `WebPassDbContext`
- `PermissionAuthorizationHandler`
- `AuditWriter`

本次增加 `IPasswordHasher` 依赖，并延续现有授权、事务、并发与审计模式。

### 3.3 认证 Cookie

现有 Cookie 已配置：

- `Secure`
- `HttpOnly`
- `SameSite=Strict`
- 登录页和拒绝访问行为

当前没有空闲期限、绝对会话上限或退出入口。登录票据只有用户 ID Claim。

### 3.4 EF 工具与部署

仓库已有本地工具 `.tools/dotnet-ef.exe` 和三个已提交迁移。现有 Windows/IIS
部署文档直接运行 `dotnet ef database update`，尚未生成或使用 migration
bundle。

## 4. 管理员创建普通用户

### 4.1 页面

在 `/admin/users` 顶部增加创建表单，只包含：

- 用户名输入框
- “Create user”提交按钮

表单提交到新的 `OnPostCreateAsync` 处理器，并使用 Razor Pages 默认防伪
令牌。

### 4.2 验证与写入

处理器首先复用 `EnsureAdministratorAsync` 验证当前数据库用户仍是管理员，
然后：

1. 去除用户名首尾空格。
2. 要求长度为 1–128 个字符。
3. 检查规范化用户名是否已存在。
4. 使用现有 `IPasswordHasher` 对固定默认密码 `abc123` 生成新的 Argon2id
   哈希。
5. 创建 `AppUser`：
   - `IsAdministrator = false`
   - `IsEnabled = true`
   - `MustChangePassword = false`
   - `FailedLoginCount = 0`
   - `LockedUntil = null`
6. 不创建任何 `UserPermission`。

数据库写入和成功审计位于同一关系型数据库事务中。除预检查外，处理器还需
识别 SQL Server 唯一约束错误 2601/2627，避免并发创建重复用户名。

### 4.3 结果与审计

成功后写入：

- `Action = UserCreate`
- `ObjectType = User`
- `ObjectId = 新用户 ID`
- `Result = Success`

审计可记录规范化用户名等非敏感信息，但不得使用包含 `password`、`hash`
等敏感名称的字段，也不得记录默认密码或密码哈希。

非法用户名和重复用户名返回当前页面并显示明确错误，不创建用户，不写成功
审计。

## 5. 管理员重置普通用户密码

### 5.1 页面

在每个非管理员用户的操作区增加“Reset password”POST 表单，提交：

- `userId`
- Base64 编码的 `rowVersion`

管理员行不显示重置按钮。

### 5.2 处理

新的 `OnPostResetPasswordAsync` 处理器：

1. 验证当前操作者仍是管理员。
2. 按 `userId` 加载目标用户。
3. 拒绝管理员目标。
4. 将提交的 `rowVersion` 设置为 EF 原始并发值。
5. 将密码替换为 `abc123` 的新 Argon2id 哈希。
6. 设置 `MustChangePassword = false`。
7. 清除 `FailedLoginCount` 和 `LockedUntil`。
8. 保存用户并写入成功审计。

用户更新和审计位于同一关系型数据库事务中。并发冲突返回 HTTP 409，目标
不存在返回现有的未找到处理路径。

成功审计使用：

- `Action = UserPasswordReset`
- `ObjectType = User`
- `ObjectId = 目标用户 ID`
- `Result = Success`

审计不包含默认密码、密码哈希或其他凭据。重置不会修改用户现有权限、启用
状态或管理员标记。

## 6. 30 分钟空闲期限与 8 小时绝对上限

### 6.1 登录票据

登录成功时除用户 ID 外，再写入一个 WebPass 私有 Claim，值为原始登录
UTC 时间的 Unix 秒数。该 Claim 随受保护的认证票据存储，不信任请求参数或
独立客户端 Cookie。

### 6.2 Cookie 配置

在现有 `Program.cs` Cookie 配置中设置：

- `ExpireTimeSpan = TimeSpan.FromMinutes(30)`
- `SlidingExpiration = true`

Cookie 中间件据此实现 30 分钟空闲期限。活跃请求可以续期票据，但续期必须
保留原始登录时间 Claim。

### 6.3 绝对上限验证

在 Cookie 的 `OnValidatePrincipal` 回调中：

1. 读取原始登录时间 Claim。
2. 缺失、格式错误或未来时间均按无效票据处理。
3. 当前 UTC 时间距原始登录时间达到或超过 8 小时时拒绝票据。
4. 拒绝后删除认证 Cookie，使下一次授权流程跳转到登录页。

滑动续期不得更新原始登录时间，因此不能延长 8 小时上限。部署此版本后，
旧版本签发且没有该 Claim 的 Cookie 会失效一次。

本次不增加数据库会话表、安全戳或每请求用户行版本验证。密码重置前已签发的
Cookie 可以继续使用，直到空闲过期、达到 8 小时、用户主动退出或 Cookie
本身失效。

## 7. 显式退出

新增 `/logout` Razor Page：

- 页面只接受 POST。
- 仅为已认证用户执行。
- 从 Claim 读取当前用户 ID。
- 写入 `Logout` 成功审计，不记录 Cookie、Claim 内容或其他敏感数据。
- 调用 Cookie `SignOutAsync`。
- 跳转到 `/login`。

共享导航对已登录用户显示 POST 退出表单，对未登录用户继续显示登录链接。
Razor Pages 默认防伪机制保护退出请求。

## 8. Migration Bundle

### 8.1 构建脚本

新增 `scripts/Build-WebPassMigrationBundle.ps1`。脚本：

- 启用严格模式并在错误时停止。
- 从脚本目录定位仓库根目录。
- 验证 `.tools/dotnet-ef.exe`、Web 项目和启动项目存在。
- 接受可选 `-OutputPath`。
- 默认输出到：
  `src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe`。
- 创建输出目录。
- 调用：
  `migrations bundle`、`--configuration Release`、
  `--target-runtime win-x64`、`--force`。
- 生成 framework-dependent bundle，不使用 `--self-contained`。
- 传入现有 Web 项目作为项目和启动项目。
- EF 返回非零状态或产物不存在时抛出错误。

默认产物位于现有 `**/bin/` 忽略规则下。自定义输出路径也只作为部署产物，
不得提交 `.exe`。

### 8.2 部署流程

更新 Windows/IIS 部署文档：

1. 从审核过的源提交发布 Web 应用。
2. 运行构建脚本，将 `WebPass.Migrations.exe` 生成到部署暂存目录。
3. 使用具备迁移权限的部署身份运行：

   ```powershell
   .\WebPass.Migrations.exe --connection "<production connection string>"
   ```

4. bundle 成功后再切换或启动 IIS 站点。
5. 运行时应用池身份不保留架构所有者或服务器管理员权限。

运行中的 Web 应用不自动执行迁移。每个发布版本必须重新生成 bundle，不能
复用旧版本的 bundle。

## 9. 错误处理

- 页面模型验证错误返回页面并显示可操作信息。
- 重复用户名由查询预检查和 SQL Server 唯一约束共同保护。
- 密码重置对管理员目标返回拒绝结果。
- 用户并发冲突返回 HTTP 409，不覆盖其他管理员的修改。
- 取消请求继续以取消语义传播，不转成业务失败。
- 会话 Claim 无效时安全退出，不向用户显示解析异常。
- bundle 构建或执行失败时停止部署，不切换 IIS。

## 10. 测试与验证

### 10.1 用户管理

- 创建普通用户时验证规范化用户名、默认字段、零权限和 Argon2id 哈希。
- 验证非法和重复用户名不产生第二行。
- 验证现有用户或管理员数量不阻止创建。
- 验证重置普通用户密码后 `abc123` 可通过现有 hasher 验证。
- 验证重置清除失败次数和锁定，但不修改权限或启用状态。
- 验证管理员目标被拒绝。
- 验证 `RowVersion` 冲突返回 409。
- 验证非管理员不能调用创建或重置处理器。
- 验证成功审计不包含默认密码或哈希。

### 10.2 会话与退出

- 验证 Cookie 期限是 30 分钟且启用滑动续期。
- 验证登录票据包含合法的原始登录时间 Claim。
- 验证 8 小时内的票据有效。
- 验证达到 8 小时的票据被拒绝。
- 验证缺失、损坏和未来时间 Claim 被拒绝。
- 验证 POST 退出清除认证状态并写入无敏感信息的审计。

### 10.3 部署

- 实际执行 bundle 构建脚本。
- 确认 `WebPass.Migrations.exe` 存在且未被 Git 跟踪。
- 对测试 SQL Server 执行 bundle 并核对迁移历史。
- 运行 `has-pending-model-changes`，预期无模型变化。
- 运行完整 Release Unit、Integration 和 SQL Server 测试。

## 11. 不在本次范围

- ASP.NET Core Identity、Active Directory 或单点登录。
- 强制改密、自助改密、随机临时密码、邮件或消息通知。
- 数据库会话表、集中式会话或主动撤销目标用户现有 Cookie。
- 管理员账号密码重置。
- 新数据库字段或 EF migration。
- 自动在 Web 应用启动时执行 migration。
- 提交生成的 migration bundle 二进制。

## 12. 已接受风险

- `abc123` 是所有内部人员已知的固定默认密码，且不会强制修改。
- 密码重置后，目标用户现有会话仍可继续到空闲过期、8 小时上限或主动退出。
- 新版本上线时旧 Cookie 会因缺少原始登录时间 Claim 而失效一次。
- migration bundle 与具体源版本绑定；复用旧 bundle 可能遗漏迁移，因此每个
  发布版本必须重新生成。
