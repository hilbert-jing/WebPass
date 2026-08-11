# WebPass 部署文档收敛实施计划

> **供智能代理执行：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，逐项执行本计划。所有步骤均使用复选框（`- [ ]`）跟踪。

**目标：** 用一份可直接执行的简体中文 `DEPLOYMENT.md` 取代所有仍在使用的生产部署说明，并只保留已批准的 Windows/IIS 三阶段离线发布路径。

**架构：** 联网准备机制作绑定源码提交的离线依赖包；位于同一提交的离线构建机生成网站、管理员工具、migration bundle 和 IIS 初始化脚本；IIS 服务器只接收这些版本化发布产物。仓库根目录手册负责完整操作流程，`README.md` 只保留入口链接，历史设计规格和实施计划保持不变。

**技术栈：** Markdown、PowerShell 5.1+、Git、.NET 10 SDK、EF Core/dotnet-ef 10.0.0、IIS WebAdministration、SQL Server 2025 Express、Windows `win-x64` 框架依赖发布。

## 全局约束

- `DEPLOYMENT.md` 是唯一仍在使用的生产部署手册，使用简体中文编写。
- 其二级标题严格依次为：`环境要求`、`数据库准备`、`配置文件`、`构建`、`部署`、`启动`、`验证`、`回滚`。
- 唯一生产路径为：联网准备机 → 离线构建机 → IIS/部署服务器。
- 本次仅修改文档，不重新生成 `WebPassMigrationOfflineKit`、发布目录或 `WebPass.Migrations.exe`。
- 不修改应用运行时行为、迁移、实体、身份验证、授权或 UI 代码。
- 历史规格和计划即使提到已被替换的路径也保持不变。
- 所有仓库编辑均使用 `apply_patch`，并保持 UTF-8 编码。
- 每项验证在对应变更后只运行一次；只有失败时才修正原因并重跑该项。

## 文件职责

- 新建 `DEPLOYMENT.md`：唯一生产手册及操作命令顺序。
- 修改 `README.md`：移除其他生产部署说明，只链接到唯一手册。
- 修改 `scripts/Initialize-WebPass.ps1`：完成提示改为指向 `DEPLOYMENT.md`。
- 删除 `docs/deployment/acceptance-test-record.md` 及 `_ZH.md`：验收项并入唯一手册的验证章节。
- 删除 `docs/deployment/certificates-and-key-recovery.md` 及 `_ZH.md`：证书要求并入环境、配置、验证和回滚章节。
- 删除 `docs/deployment/windows-server-iis.md` 及 `_ZH.md`：由根目录手册取代竞争性流程。
- 在 `docs/superpowers/plans/` 新建本计划的英文版和中文版，作为历史执行记录保留。

---

### 任务 1：创建唯一生产部署手册

**文件：** 新建 `DEPLOYMENT.md`。

**接口：**
- 输入：三个部署脚本、两个项目文件、`src/WebPass.Web/appsettings.json` 和 `/health` 的实际行为。
- 输出：包含精确机器边界、发布产物、配置键、SQL 角色、IIS 命令、验证门禁和回滚边界的唯一部署入口。

- [ ] **步骤 1：证明当前尚不存在符合要求的唯一手册。** 检查 `DEPLOYMENT.md`，预期因文件不存在而失败。
- [ ] **步骤 2：编写八章节手册。** 严格使用全局约束中的八个二级标题，并写入英文计划任务 1 所列的环境、数据库、配置、构建、部署、启动、验证和回滚约定；所有命令使用仓库现有脚本接口。
- [ ] **步骤 3：验证标题与接口。** 使用英文计划任务 1 的 PowerShell 检查，确认八个标题顺序正确，并包含三个脚本、两个可执行文件、生产配置文件、IIS 应用程序池身份和 `/health`。
- [ ] **步骤 4：提交唯一手册。**

```powershell
git add -- DEPLOYMENT.md
git commit -m "docs: add canonical WebPass deployment runbook"
```

---

### 任务 2：移除相互竞争的部署说明

**文件：** 修改 `README.md`；删除 `docs/deployment/` 下六份受跟踪文档。

**接口：**
- 输入：任务 1 的 `DEPLOYMENT.md`。
- 输出：README 中只保留一个部署入口，不再存在第二份仍在使用的部署手册。

- [ ] **步骤 1：用唯一链接替换 README 中的生产流程。** 保留产品、开发、本地运行、构建测试、安全、结构、设计和范围说明；删除离线部署小节、Windows Server 发布摘要和三个旧部署链接；新增简短的 `## 生产部署` 并只链接一次 `DEPLOYMENT.md`；目录树改为显示根目录手册。
- [ ] **步骤 2：删除六份重复文档。** 使用一次精确 `apply_patch` 删除任务文件列表中的六个文件，不删除 `docs/superpowers/` 历史记录。
- [ ] **步骤 3：验证唯一入口。** 运行英文计划任务 2 的引用检查；此时只允许因任务 3 尚未更新脚本提示而失败。
- [ ] **步骤 4：提交文档收敛。**

```powershell
git add -- README.md docs/deployment
git commit -m "docs: remove duplicate deployment guidance"
```

---

### 任务 3：更新初始化脚本的交接提示

**文件：** 修改 `scripts/Initialize-WebPass.ps1`。

**接口：**
- 输入：`DEPLOYMENT.md` 的验证章节。
- 输出：不会再把操作人员引向已删除文件的完成提示。

- [ ] **步骤 1：替换过期提示。** 仅把指向 `docs/deployment/acceptance-test-record.md` 的 `Write-Host` 改为：

```powershell
Write-Host 'Complete the verification section in DEPLOYMENT.md from a trusted LAN client.'
```

- [ ] **步骤 2：解析 PowerShell。** 运行英文计划任务 3 的 AST 解析命令，预期没有语法错误。
- [ ] **步骤 3：重新运行唯一入口检查。** 预期全部通过。
- [ ] **步骤 4：提交脚本引用修正。**

```powershell
git add -- scripts/Initialize-WebPass.ps1
git commit -m "chore: point deployment completion to canonical runbook"
```

---

### 任务 4：执行一次与风险相称的最终验证

**文件：** 只验证；失败时回到所属任务修正，再重跑失败项。

**接口：** 输入所有任务产物，输出结构、引用、语法、空白和回归证据。

- [ ] **步骤 1：验证受跟踪部署文档状态。** 确认根目录 `DEPLOYMENT.md` 已受跟踪，`docs/deployment/` 下不再有文件。
- [ ] **步骤 2：运行标题、接口、引用和 PowerShell 解析检查。** 每项只运行一次，预期全部退出码为零。
- [ ] **步骤 3：检查空白和范围。** 运行 `git diff --check`、`git status --short` 和最近提交记录，确认没有离线包、NuGet 包、发布目录或 migration bundle 被加入。
- [ ] **步骤 4：不还原依赖地运行一次现有测试。**

```powershell
dotnet test WebPass.sln -c Release --no-restore
```

预期零失败；如果现有本地缓存不足以启动测试，只记录环境限制，不为本次纯文档变更执行联网还原。

- [ ] **步骤 5：审核最终差异。** 确认手册命令可执行、没有第二条部署路线，且不存在八个指定章节以外的其他二级标题。

## 预期提交顺序

1. `docs: add canonical WebPass deployment runbook`
2. `docs: remove duplicate deployment guidance`
3. `chore: point deployment completion to canonical runbook`

已批准的双语设计提交和本次双语计划提交位于上述三个实施提交之前。

