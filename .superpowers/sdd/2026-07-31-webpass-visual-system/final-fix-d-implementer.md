# Final Review Fix D 实施报告：治理表单反馈与中文标签

## 结果

- 创建用户的空用户名、超过 128 个字符和重复用户名现在返回固定、可行动的简体
  中文字段错误；错误紧邻用户名控件呈现。
- 用户名无效时，控件输出 `aria-invalid="true"`，并通过
  `aria-describedby="username-error"` 关联字段错误。字段错误本身不使用 live
  region。
- 用户创建、密码重置、启停和权限更新的全局成功状态不再插入用户名；既有预设
  密码工作流保留，但全局状态和审计明细不呈现预设密码、密码哈希或原始异常。
- 审计页通过 `UiLabels` 映射动作、对象类型和结果，不再把内部代码作为主要文案；
  未知值固定回退为“未知操作”“未知对象”“未知结果”，不会回显任意内部代码。
- Server Edit 并发快照的状态复用 `UiLabels.ForAliveStatus`，与服务器列表统一为
  “未知 / 存活 / 异常 / 停用”。
- 管理员页面 policy、handler 级管理员检查、防伪令牌、审计写入、事务边界、重复
  用户竞态处理、启停/重置/权限语义均未改变。

## 实际审计代码盘点

基于生产代码中所有 `AuditEntry` 写入路径及其动态 action/result 来源，测试和映射
覆盖：

- 动作 22 个：`AssetCreate`、`AssetEdit`、`AssetArchive`、
  `AdministratorPasswordExport`、`AssetExport`、`ImportCommit`、
  `PingExecute`、`StatusMarkAlive`、`DataKeyRotate`、
  `SecretReauthentication`、`SecretReveal`、`SubnetCreate`、`SubnetEdit`、
  `SubnetEnable`、`SubnetDisable`、`SubnetDelete`、`Login`、`Logout`、
  `UserCreate`、`UserPasswordReset`、`UserEnablement`、
  `UserPermissionsReplace`。
- 对象类型 5 个：`ServerAsset`、`ImportJob`、`DataEncryptionKey`、`User`、
  `Subnet`。
- 结果 10 个：`Success`、`Denied`、`Failure`、`Timeout`、`Unreachable`、
  `InternalError`、`NotFound`、`InvalidCredentials`、`Locked`、`Disabled`。

## TDD 证据

### RED / GREEN 1：字段反馈、审计页面与 Server 状态

生产修改前运行聚焦行为测试，7/7 失败：

```text
dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj \
  -c Release --no-restore \
  --filter "FullyQualifiedName~AdminUsersTests.Create_rejects|FullyQualifiedName~VisualSystemPageTests.Governance_pages_render|FullyQualifiedName~VisualSystemPageTests.Administrator_user_validation|FullyQualifiedName~AssetAndPingTests.Edit_conflict_preserves"
```

失败分别显示：字段错误仍为英文、用户名控件缺少 `aria-invalid`、审计页面仍输出
`UserPermissionsReplace`、Edit 快照仍输出“故障”。最小实现后同一集合 7/7
通过。

### RED / GREEN 2：所有真实 emit code

在只实现种子路径和未知回退后，加入实际 emit 盘点表并运行：

```text
dotnet test tests/WebPass.UnitTests/WebPass.UnitTests.csproj \
  -c Release --no-restore \
  --filter "FullyQualifiedName~UiLabelsTests.Emitted_audit"
```

结果为 37 个样例中 34 个失败，均因尚未映射的真实代码落入固定未知回退。补全
映射后，完整 `UiLabelsTests` 65/65 通过（包含 null 和攻击者控制未知值回退）。

### RED / GREEN 3：全局 live 状态去除用户名

创建、重置、权限更新和停用四个既有业务测试先改为固定状态文案及“不得包含目标
用户名”断言。生产修改前 0/4 通过，实际值均包含 `operator`；移除用户名插值后
4/4 通过。

## 自动化验证

聚焦 Release：

```text
dotnet test WebPass.sln -c Release --no-restore \
  --filter "FullyQualifiedName~AdminUsersTests|FullyQualifiedName~PermissionTests|FullyQualifiedName~PermissionRouteTests|FullyQualifiedName~AuditWriterTests|FullyQualifiedName~UiLabelsTests|FullyQualifiedName~VisualSystemPageTests.Governance_pages_render|FullyQualifiedName~VisualSystemPageTests.Administrator_user_validation|FullyQualifiedName~AssetAndPingTests.Edit_"
```

结果：Unit 67/67、Integration 27/27 通过。

完整 Release：

```text
dotnet test WebPass.sln -c Release --no-restore
```

结果：Unit 145/145、Integration 196/196 通过；0 failed、0 skipped。

`git diff --check` 通过。

## 范围与安全核对

- 生产修改仅限 Admin Users Razor Page/PageModel、Audit Razor Page、Server Edit
  Razor Page 和 `UiLabels`。
- 没有修改权限策略、授权处理器、防伪配置、数据库模型/迁移、审计 writer、密码
  hasher 或用户业务事务流程。
- 字段错误和未知审计回退均为固定中文；用户名输入不会回填到失败表单，全局 live
  状态只输出固定成功/错误摘要。
- 审计对象 ID 和关联编号仍按既有只读业务需求呈现；动作、对象类型和结果的任意
  未知内部代码不再作为主要文案呈现。
- 既有未跟踪 `.playwright-cli/` 与 `output/` 内容未修改、删除或暂存。
