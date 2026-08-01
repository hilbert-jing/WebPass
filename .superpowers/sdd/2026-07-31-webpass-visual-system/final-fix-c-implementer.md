# Final Review Fix C 实施报告：服务器编辑并发草稿

## 结果

- 服务器编辑发生乐观并发冲突时返回 HTTP 409，并保留用户提交的编辑草稿。
- 冲突分支不再调用 `ModelState.Clear()` 或 `OnGetAsync` 覆盖绑定值；只移除
  `rowVersion` 的 ModelState 项，并把隐藏 token 更新为数据库当前版本。
- 页面使用独立的 `ServerSnapshot` 只读模型呈现数据库最新值，与可编辑草稿明确
  分区。最新快照不会自动写入草稿。
- 快照覆盖业务 IP、位置、状态、计算机名、系统名称、操作系统版本、数据库版本和
  备注，不包含密码、密文或哈希。密码输入继续为可选密码字段，且不从数据库载入。
- 用户必须检查两组值并用新 token 明确再次提交；首次冲突不会修改数据库，显式
  重试成功后才写入草稿。
- 编辑权限检查、防伪令牌、ArgumentException 的 BadRequest、未授权 Forbid、
  固定中文冲突提示和既有安全消息均保留。

## TDD 证据

### RED

先把旧的“冲突时只显示数据库值并丢弃草稿”测试替换为两个真实 HTTP 场景：

1. GET 编辑页取得旧 rowVersion 和防伪令牌；另一数据库上下文写入一组完整当前值
   并把 rowVersion 从 `[1]` 更新为 `[2]`；POST 另一组完整草稿触发冲突。
2. 第一次冲突后检查数据库仍为并发方值；从 409 页面提取新 rowVersion 和新防伪
   令牌，显式再次 POST 同一草稿并检查成功落库。

生产代码修改前运行：

```text
dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj \
  -c Release --no-restore --filter "FullyQualifiedName~Edit_conflict"
```

结果：0 passed / 2 failed。两条都在 `Expected: Conflict; Actual: OK` 处失败，证明
测试命中了现有错误冲突分支，而不是编译或夹具错误。

### GREEN

最小实现后用同一命令运行：2 passed / 0 failed。

测试逐项证明：

- 409 响应中的可编辑表单保留草稿全部非敏感资产字段及选择状态；
- 独立只读区域同时呈现另一组数据库当前值；
- hidden rowVersion 是 `[2]` 的 Base64，而不是提交的 stale `[1]`；
- 首次冲突后数据库未发生静默覆盖；
- 使用响应中的新 token 和防伪令牌明确重试返回 302，并在此时才把完整草稿写入
  数据库。

## 自动化验证

聚焦 Release：

```text
dotnet test WebPass.sln -c Release --no-restore \
  --filter "FullyQualifiedName~Edit_|FullyQualifiedName~PermissionRouteTests|FullyQualifiedName~ProductionSecurityTests|FullyQualifiedName~AssetSecret"
```

结果：Integration 15/15、Unit 1/1 passed。

完整 Release：

```text
dotnet test WebPass.sln -c Release --no-restore
```

结果：Integration 196/196、Unit 102/102 passed；0 failed、0 skipped。

`git diff --check` 通过。

## 范围与安全核对

- 生产修改仅限服务器编辑 Razor Page 及其 PageModel。
- 测试修改仅限既有 `AssetAndPingTests` 的编辑并发端点场景与测试夹具。
- 没有修改数据库架构、迁移、`ServerAssetService`、权限策略或全局防伪配置。
- 冲突快照从 `AsNoTracking` 查询加载，与失败请求中仍被 EF 跟踪的草稿实体隔离。
- 页面输出继续由 Razor 编码；没有呈现内部异常详情、密码、密文或哈希。
- 现有 `.playwright-cli/` 与 `output/` 未跟踪内容未修改、删除或暂存。
