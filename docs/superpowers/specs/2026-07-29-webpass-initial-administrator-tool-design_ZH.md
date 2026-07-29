# WebPass 初始管理员工具设计

**日期：** 2026-07-29

## 背景

WebPass 已具备本地 `AppUser` 实体、SQL Server 持久化、Argon2id 密码哈希，以及基于 `AppUser.IsAdministrator` 的管理员授权。已部署的应用程序没有受支持的工具可在 Web 界面之外创建管理员账户。

本次需要新增一个本地命令行工具，直接在配置的 WebPass 数据库中创建管理员。这是部署工具，而不是 Web 终结点或管理员管理系统。

## 范围

该工具：

- 每次成功调用时创建一个已启用的管理员；
- 无论 `Users` 为空，还是已有用户或管理员，均可运行；
- 通过命令行参数接受连接字符串和用户名；
- 通过隐藏的交互式控制台输入读取并确认密码；
- 复用 WebPass 现有的 `WebPassDbContext`、`AppUser` 和 Argon2id 密码哈希器；
- 返回清晰的进程退出代码和不含机密的结果消息。

## 不在范围内

- 检查数据库是否已有用户或管理员。
- 限制管理员数量。
- 创建或编辑普通用户。
- 密码重置或更改密码流程。
- 分配普通 `UserPermission` 记录。
- Web 页面、HTTP 终结点、启动钩子或后台服务。
- 数据库架构变更或 EF Core 迁移。
- 新的密码哈希算法或密码策略。
- 对登录、授权、Cookie、会话或现有审计的更改。

## 架构

新增一个独立的 `.NET 10` 控制台项目：

```text
src/WebPass.AdminInit/
  WebPass.AdminInit.csproj
  Program.cs
  AdministratorInitializer.cs
```

该项目引用 `WebPass.Web`，以便使用现有领域实体、EF Core 上下文和 `Argon2PasswordHasher`，无需复制身份验证逻辑。它会加入 `WebPass.sln`，并与 Web 应用程序独立发布。

`Program.cs` 负责参数解析、隐藏密码输入、进程退出代码和面向用户的消息。`AdministratorInitializer` 负责输入规范化、重复用户名检测、哈希、实体构造及单次数据库写入。

不在 `WebPass.Web` 中注册初始化代码，Web 应用程序也不调用该工具。

## 命令契约

示例：

```powershell
WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

随后该工具会提示：

```text
密码：
确认密码：
```

密码字符不会回显。拒绝重定向的标准输入，避免明文密码意外经由命令管道传入或被命令历史记录捕获。

必需参数：

- `--connection-string`：SQL Server 连接字符串。
- `--username`：管理员用户名。

未知参数、缺少值或重复参数均属于无效用法。

## 创建流程

1. 解析必需参数。
2. 修剪用户名，要求其非空且最多 128 个字符。
3. 以不回显方式读取两次密码。
4. 要求密码非空，且两次输入完全一致。
5. 使用提供的 SQL Server 连接字符串打开 `WebPassDbContext`。
6. 仅检查规范化后的用户名是否已存在。
7. 使用现有 `Argon2PasswordHasher` 哈希密码。
8. 插入一个具有以下属性的 `AppUser`：
   - `Username` 设为规范化后的用户名；
   - `PasswordHash` 设为 Argon2id 结果；
   - `IsAdministrator = true`；
   - `IsEnabled = true`；
   - `MustChangePassword = false`；
   - `FailedLoginCount = 0`；
   - `LockedUntil = null`。
9. 只保存一次，并报告已创建的用户名。

不创建 `UserPermission` 行。现有管理员授权继续使用 `IsAdministrator`。

## 失败处理

对于无效参数、无效用户名、空密码、确认密码不匹配或重复用户名，该工具不做任何数据库更改。

退出代码：

- `0`：管理员已创建。
- `2`：无效命令用法或交互式输入。
- `3`：用户名已存在。
- `1`：数据库或意外运行故障。

错误输出标识错误类别，但不包含密码、密码哈希、连接字符串或可能含敏感配置的数据库异常详情。

若并发执行使用相同用户名，数据库唯一索引仍为最终权威。失败的一次调用会报告用户名重复，且不会创建第二条记录。

## 安全边界

- 密码仅存在于本地控制台进程和哈希调用中。
- 密码和哈希值绝不打印，也不放入参数、文件、日志或审计负载中。
- 必须由已具有数据库写入权限的操作人员在本机执行该工具。
- 该可执行文件不授予任何额外的 SQL Server 或 Windows 权限。
- 有意允许重复运行，并且可为不同用户名创建多个管理员。
- 该工具不添加审计事件：此限定的部署命令在已认证 WebPass 会话之外运行，且未要求审计行为。

## 数据库影响

不添加实体、表、列、索引或迁移。复用现有 `Users.Username` 唯一索引。

## 测试

自动化测试覆盖：

- 创建一个已启用管理员，且其密码可通过现有哈希器验证；
- 已有普通用户时创建管理员；
- 已有另一个管理员时创建管理员；
- 拒绝重复用户名，且不产生第二条记录；
- 拒绝空用户名或超长用户名；
- 在访问数据库前拒绝空密码或确认密码不匹配；
- 参数解析和稳定的退出代码映射；
- 结果和错误消息绝不包含密码或哈希文本。

完整 Release 测试套件仍是身份验证、授权、审计、安全数据操作和生产托管的回归门槛。

## 部署文档

更新 IIS 部署运行手册，加入：

- `WebPass.AdminInit` 的独立发布命令；
- 使用集成式 SQL Server 身份验证的本地执行示例；
- 确认：若操作人员不再需要该工具，可在创建账户后从服务器删除它。
