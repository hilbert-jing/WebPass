# WebPass 审计操作人用户名设计规格

## 目标

审计事件同时保存稳定的操作人用户 ID 和事件写入时可见的用户名；`/audit` 显示用户名；数据库迁移时回填生产环境中能够解析的历史记录。

## 范围

本次只修改审计持久化、审计页面、对应 EF Core migration 和必要测试。登录、密码、权限、Secret、Ping、导入导出、子网及服务器资产行为保持不变。

## 当前行为与根因

`AuditLog` 只保存可空的 `ActorUserId`，`AuditWriter` 也只复制该 ID，`/audit` 直接渲染 `ActorUserId.ToString()`。因此管理员看到 GUID，数据库中也没有不可变的用户名快照。

## 选定设计

新增可空的 `AuditLog.ActorUsername`，映射为与 `Users.Username` 一致的 `nvarchar(128)`；`ActorUserId` 保持不变。`AuditWriter` 在存在操作人 ID 时集中从 `Users` 解析用户名，并与审计记录在同一次保存中写入。用户不存在时快照保持空值，但不阻止审计写入。

审计页面按以下顺序显示：

1. 非空白的 `ActorUsername`；
2. `ActorUserId` 为空时显示“系统”；
3. 有 ID 但无法解析快照时显示“未知用户（{ActorUserId}）”。

`AuditLogs` 与 `Users` 之间不增加外键。即使用户行缺失，审计记录也必须可读；稳定 ID 的保存不依赖用户生命周期。

## Migration 与历史回填

Migration 先增加可空的 `ActorUsername`，再通过 SQL Server 联表更新，把 `AuditLogs.ActorUserId` 对应的 `Users.Username` 写入新列。操作人 ID 为空或找不到用户的行保持空值，因此系统事件和孤立历史 ID 不会导致迁移失败。`Down` 只删除新列。

历史回填只能得到执行 migration 时的当前用户名；只有新审计记录能够保存真正的事件时快照。

## 未采用方案

- 让所有 `AuditEntry` 调用点传入用户名虽然省去一次查询，但改动面大、容易产生 ID 与名称不一致。
- 只在 `/audit` 渲染时关联 `Users` 无法形成快照，用户改名或删除后会丢失原名称。
- 必填列或外键会破坏系统事件和孤立审计记录的兼容性。

## 测试

测试刻意保持最小：

- 一个 `AuditWriter` 测试证明能够保存已解析快照，且用户缺失不会阻止写入；
- 在现有 `/audit` 页面测试中验证用户名、系统事件及未知用户降级显示；
- 一个 SQL Server migration 测试证明匹配历史数据被回填，同时系统和孤立记录继续可读且 ID 不变。

先运行聚焦测试，再根据可用依赖环境执行解决方案构建、完整测试及 EF migration 一致性检查。

## 部署与回滚

`DEPLOYMENT.md` 已要求干净构建、为同一提交重新生成 migration bundle、停站、验证完整数据库备份，并在切换版本前成功执行 bundle，因此无需修改部署文档。数据库回滚遵循文档中的完整备份恢复边界，不临时编写反向 SQL。

## 风险

- 每次带操作人 ID 的审计写入会增加一次用户查询；
- 历史用户名可能是 migration 执行时而非事件发生时的值；
- 现有未知用户名登录事件的 `ActorUserId` 为空，仍会按系统事件显示；登录行为不在本次范围内；
- 当前本地离线包源不完整，源码重建验证可能需要仓库部署流程规定的离线依赖包。
