# WebPass 部署文档收敛设计

**日期：** 2026-08-11

**状态：** 已批准设计

## 目标

用仓库根目录的一份中文规范文档 `DEPLOYMENT.md` 取代相互竞争的生产部署说明。
该文档只描述一条可重复执行的 Windows Server/IIS 部署路径，并且顶层内容仅包括：
环境要求、数据库准备、配置文件、构建、部署、启动、验证和回滚。

## 现状审计

当前受 Git 跟踪的部署说明分散在 `README.md` 和 `docs/deployment/` 下三组
中英文文档中，无法组成一条无歧义的执行流程：

- `README.md` 推荐在联网构建机上直接生成部署产物，IIS 手册却要求依次使用
  联网准备机、离线构建机和 IIS 服务器。
- IIS 手册声明服务器不需要 .NET SDK 或 `dotnet-ef`，但管理员初始化步骤又展示了
  `dotnet publish`，既未明确该命令应在构建机运行，也未说明如何传输其输出。
- 首次部署使用固定的 `C:\WebPass\staging`，回滚步骤却假定产物位于版本化目录。
- 数据库准备只说明权限原则，没有给出数据库、Windows 登录名、数据库用户和
  运行时角色的完整创建命令。
- 生产配置列出了必要设置，却没有规定唯一配置文件、准确存放位置和完整内容。
- 中文 IIS 手册链接到英文证书与验收文档，六份平行文档容易继续发生内容漂移。
- `README.md` 重复了一套不完整的生产部署步骤，操作人员可能不打开正式手册便开始执行。

证书恢复和验收清单中的关键要求仍然有效，但用户要求的最终形态不需要独立附件。
这些部署必需内容将被吸收到 `DEPLOYMENT.md` 允许的八个章节中。

## 选定的部署架构

唯一生产路径采用仓库已经实现并由部署集成测试覆盖的三阶段离线发布流程：

1. 在联网 Windows 准备机上检出已审核提交，使用
   `Prepare-WebPassMigrationOfflineKit.ps1` 制作
   `WebPassMigrationOfflineKit`。
2. 在离线 Windows 构建机上检出同一提交，仅使用该离线包完成还原，并生成
   `win-x64` 网站、`WebPass.AdminInit.exe`、`WebPass.Migrations.exe`，以及用于
   本次发布的 `Initialize-WebPass.ps1` 副本。
3. 只把上述发布产物传输到 IIS 服务器。先以临时部署权限执行 migration bundle，
   再配置版本化发布目录、初始化或切换 IIS、启动站点，并分别在服务器和获准的
   局域网客户端完成验证。

IIS 服务器只保留 .NET 10 Hosting Bundle，不接收 .NET SDK、`dotnet-ef`、
源码检出、离线依赖包、NuGet 缓存或本地 feed。

## 唯一文档结构

`DEPLOYMENT.md` 使用简体中文编写，包含一个一级标题，并严格按以下顺序设置八个
二级标题：

1. `环境要求`
2. `数据库准备`
3. `配置文件`
4. `构建`
5. `部署`
6. `启动`
7. `验证`
8. `回滚`

章节内部可以使用更低级别标题组织命令，但不得增加其他二级主题。文档会明确声明
这是项目唯一的生产部署路径。

## 环境与安全边界

文档会按实际使用方划分环境要求：

- 联网准备机：Windows、Git、.NET 10 SDK、干净且已审核的源码检出，以及对可信
  NuGet 源的访问能力；
- 离线构建机：Windows、Git、匹配的 .NET 10 SDK、位于清单所记录提交的源码检出，
  以及已传输的离线依赖包；
- 部署服务器：受支持的 64 位 Windows Server、先于 .NET 10 Hosting Bundle
  安装的 IIS、`AspNetCoreModuleV2`、本机 SQL Server 2025 Express、PowerShell
  5.1 或更高版本、固定 IPv4 地址和已批准的局域网 CIDR。

部署服务器在 `LocalMachine\My` 中使用两张独立证书：一张 SAN 与客户端访问地址
匹配的 HTTPS 证书，以及一张可导出私钥的 RSA 数据加密证书。只有
`IIS AppPool\WebPass` 和获准管理员可以读取数据证书私钥。部署验收前必须制作并
测试一份加密的离线 PFX 恢复副本。

## 数据库与配置约定

数据库准备会给出可直接执行的 SQL，用于：

- 创建本机 `WebPass` 数据库；
- 为部署操作员创建 Windows 登录名和数据库用户；
- 在执行 migration bundle 和创建初始管理员期间，临时把部署用户加入 `db_owner`；
- 创建 `IIS APPPOOL\WebPass` 登录名和数据库用户；
- 只向运行时身份授予 `db_datareader` 和 `db_datawriter`；
- 部署结束后移除部署用户的 `db_owner` 成员资格。

网站以 `<版本化发布目录>\site\appsettings.Production.json` 作为唯一生产配置文件。
该文件包含使用 Windows 集成身份验证的本机数据库连接字符串、正数 Ping 限制和
数据加密证书指纹。HTTPS 证书指纹只作为 IIS 绑定参数，绝不能写入
`SecretEncryption:CertificateThumbprint`。

## 发布、启动与回滚约定

每个已审核提交都生成独立发布目录，并传输到
`C:\WebPass\releases\<commit>`。其中的 `site`、`admin`、migration bundle 和
`Initialize-WebPass.ps1` 必须来自同一提交。执行迁移前必须完成 SQL Server
完整备份。

首次部署由 `Initialize-WebPass.ps1` 创建专用应用程序池、仅 HTTPS 站点、证书
私钥 ACL 和仅限局域网的防火墙规则。后续部署先停止站点，把 IIS 物理路径切换到
新的版本化 `site` 目录，再显式启动。初始管理员工具从已传输的 `admin` 目录在
IIS 服务器本机运行，首次成功使用后删除。

应用回滚通过把 IIS 切回保留的上一版本完成。数据库架构不得临时执行降级操作。
如果新版本迁移不向后兼容，站点必须保持停止，直到已审核的部署前数据库备份与匹配的
旧版本一起恢复。导出的 CSV/XLSX 文件永远不能作为数据库备份。

## 仓库变更

- 创建 `DEPLOYMENT.md`，作为唯一仍在使用的生产部署手册。
- 删除 `docs/deployment/` 下全部六份受跟踪文档。
- 从 `README.md` 移除生产部署步骤和旧链接，只保留一个指向 `DEPLOYMENT.md` 的入口，
  并同步更新项目目录树。
- 把 `scripts/Initialize-WebPass.ps1` 的完成提示从已删除的验收记录改为
  `DEPLOYMENT.md`。
- 现有历史设计规格和实施计划记录的是当时的文件路径，即使提到旧路径也予以保留。
- 不修改应用运行时行为、数据库迁移、实体、身份验证、授权或 UI 代码。

## 验证

验证内容包括：

- `DEPLOYMENT.md` 只有八个指定二级标题，标题名称和顺序完全符合要求，并且只描述
  选定的三阶段路径；
- 所有命令、文件路径、配置键、目标框架、运行时标识符、脚本参数和健康检查响应均与
  当前仓库一致；
- 网站和管理员工具都在传输前由离线构建机生成，同时包含匹配的 IIS 初始化脚本；
- 仍在使用的文档和脚本不再引用任何已删除部署文件；
- IIS 初始化 PowerShell 脚本仍可通过语法解析；
- Markdown 链接可解析，`git diff --check` 通过；若本地依赖缓存满足条件，现有解决方案
  测试在不还原依赖的情况下通过。

## 验收标准

- 新操作人员只需遵循 `DEPLOYMENT.md`，即可完成环境准备、启动验证和回滚。
- 说明不会要求在 IIS 服务器安装 SDK、保留源码、NuGet 包缓存或 `dotnet-ef`。
- 网站、管理员工具、migration bundle 和 IIS 初始化脚本明确来自同一个已审核提交。
- 迁移结束后，运行时数据库身份只有数据读写权限。
- 旧部署文档及仍在使用的文件中指向它们的链接全部删除。
- `DEPLOYMENT.md` 不包含八个指定章节之外的其他顶层主题。

