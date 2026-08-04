# WebPass 离线迁移捆绑包设计

**日期：** 2026-08-03

**状态：** 批准的设计

## 目标

 为具有匹配的 .NET 10 SDK 但无法访问 `nuget.org` 并且没有内部 NuGet 源的 WebPass 构建计算机提供可重复的迁移捆绑工作流。经批准的离线依赖包在连接互联网的计算机上准备好一次，通过可移动媒体或内部文件共享传输，并且永远不会提交给 Git。

生成的 `WebPass.Migrations.exe` 仍然是依赖于框架的 `WebPass.Migrations.exe` EF Core 迁移捆绑包。它是根据经过审查的 WebPass 源提交构建的，并通过部署标识应用于本地 SQL Server 实例。

## 范围

此更改包括：

- a PowerShell 脚本，用于在连接的 Windows 计算机上准备离线依赖项工具包；
-  现有迁移包构建脚本中严格的离线支持；
- 同步英文和简体中文Windows/IIS部署说明；
- SQL Server 集成覆盖率，生成并执行捆绑包；
- ignore 将离线包和生成的可执行文件保留在 Git 之外所需的规则。

此更改不会修改 WebPass 用户界面、身份验证、授权、审核、实体、`WebPassDbContext`、模型快照或现有迁移。它不添加数据库备份、自动部署、内部包服务器或应用程序端迁移执行。

## 环境假设

- 连接的准备机和离线构建机运行Windows。
- 均具有与 WebPass 兼容的 .NET 10 SDK。
- 准备机可以访问受信任的NuGet源。
- 离线构建机器无法访问`nuget.org`并且没有内部NuGet源。
- SQL Server Express 是部署环境本地的。
-  离线工具包通过操作员控制的介质或内部文件共享进行传输，并且不会签入 Git。
- 部署服务器已具有依赖于框架的 WebPass 部署所需的 .NET 10 托管捆绑包。

## 精选架构

### 连接准备脚本

添加：

```text
scripts/Prepare-WebPassMigrationOfflineKit.ps1
```

接口：

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath <offline-kit-directory> `
  [-Force]
```

脚本在唯一的暂存目录中构建套件。仅在套件通过自己的离线验证后，它才会发布目标目录。如果没有 `-Force`，则现有目标是错误的。对于 `-Force`，仅在新的分阶段套件完成后才会进行更换，因此失败的准备不会破坏之前有效的套件。

的准备流程为：

1. 验证源树、.NET 10 SDK、Git 提交和输出路径。
2.  将 `dotnet-ef 10.0.0` 准确安装到暂存的 `dotnet-ef 10.0.0` 目录中。
3. 将 WebPass 和 `win-x64` 依赖项恢复到隔离的 NuGet 全局包目录中。
4. 生成临时迁移捆绑包以强制恢复 EF 捆绑包发布步骤使用的依赖项。
5. 将所需的 `.nupkg` 文件收集到本地源中。
6. 重新创建一个空的验证缓存并再次生成仅包含本地提要的包。
7. 删除验证包并编写最终清单。
8. 将经过验证的暂存目录移动到请求的输出位置。

准备脚本可以使用配置的可信网络源。最终套件不得保留网络包源。

### 离线套件布局

```text
WebPassMigrationOfflineKit/
|-- manifest.json
|-- NuGet.Config
|-- tools/
|   |-- dotnet-ef.exe
|   `-- .store/...
|-- packages/
|   `-- <expanded NuGet global-packages content>
`-- feed/
    `-- <required .nupkg files>
```

`NuGet.Config` 清除继承的包源并仅启用套件的本地源。清单包含：

- offline-kit格式版本；
- source Git 提交；
- `dotnet-ef`版本`dotnet-ef`；
- SDK主要版本`10`；
- 目标运行时`win-x64`；
-  UTC 格式的创建时间戳。

离线工具包既不包含WebPass源代码也不包含最终的生产迁移包。

### 离线捆绑构建

扩展：

```text
scripts/Build-WebPassMigrationBundle.ps1
```

界面：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>] `
  [-OfflineKitPath <offline-kit-directory>]
```

当省略 `-OfflineKitPath` 时，现有的开发人员工作流程仍然存在：脚本使用 `-OfflineKitPath` 并且不会隐式安装工具。

当提供 `-OfflineKitPath` 时，脚本：

1. 验证清单和所需的套件目录；
2. 需要`dotnet-ef 10.0.0`、SDK 10和`dotnet-ef 10.0.0`的清单值；
3. 要求清单源提交等于当前源`HEAD`；
4. 执行套件的`dotnet-ef --version`并检查确切的版本；
5. 暂时指向`NUGET_PACKAGES`并仅在套件上恢复源；
6.  对此严格离线操作禁用网络源回退和 NuGet 审核网络访问；
7. 在临时输出中生成依赖于框架的`win-x64`包；
8. 生成成功后才替换请求的输出；
9. 恢复`finally`中所有更改的进程环境变量。

A 丢失包、无效清单、不匹配的提交、错误的工具版本或丢失输出是终止错误。该脚本不会修改用户级别或计算机级别的 NuGet 配置。

默认输出保持：

```text
src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe
```

## 部署流程

英语和简体中文 Windows/IIS 操作手册描述了相同的三阶段过程。

### 1。连接准备机

- 查看经过审查的 WebPass 源代码提交。
- 运行准备脚本。
- 将整个生成的离线套件传输到离线构建机器。

### 2。离线构建机

- 查看相同的已审查源提交。
- 使用 `-OfflineKitPath` 运行捆绑脚本。
- 如果清单验证、恢复或捆绑包生成失败，则停止。
-  仅将网站发布输出和 `WebPass.Migrations.exe` 传输到部署服务器。

### 3。部署服务器

- 不要安装.NET SDK或`dotnet-ef`。
-  使用可以更改 WebPass 数据库的部署标识运行 `WebPass.Migrations.exe --connection <connection-string>`。
- 如果迁移执行失败，则停止部署。
-  迁移后从运行时 IIS 标识中删除架构所有者或服务器管理员权限。

 该捆绑包会为每个经过审核的版本重新生成。仅当其源代码提交、SDK 主要版本、运行时标识符和依赖项集仍与构建源匹配时，才能重用离线工具包。

## 测试

扩展现有 SQL Server 集成覆盖范围：

```text
tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
```

端到端测试通过以下两种方式之一获取套件：

- 如果设置了`WEBPASS_MIGRATION_OFFLINE_KIT`，则使用传输的套件；
- 否则调用准备脚本以使用开发或 CI 计算机配置的包源创建临时套件。

然后：

1. 以严格离线模式构建`WebPass.Migrations.exe`；
2.  针对唯一命名的本地 SQL Server 数据库执行捆绑包；
3. 将`WebPassDbContext`已知的迁移与SQL Server中记录为应用的迁移进行比较；
4. 删除 `finally` 块中的数据库、生成的捆绑包和测试拥有的临时套件。

额外的重点测试验证拒绝：

- an 清单不完整或缺少套件目录；
- a 清单包含错误的 `dotnet-ef` 版本；
- a 套件缺少依赖项；
- a 源提交不匹配。

 失败测试必须确认没有留下任何输出包，并且构建不会回退到 HTTP 包源。这些测试仅是功能测试；不添加负载或压力测试。

## 安全和维护约束

-  离线包、本地缓存、工具二进制文件、临时捆绑包和最终捆绑包将被 Git 忽略。
- 没有连接字符串、密码、包源凭据或其他机密写入清单或构建日志。
- 脚本仅接受显式文件系统路径，并在替换或清理之前验证确切的目标目录。
- 临时目录是唯一的，仅删除其确切解析路径。
- 工具和包版本已修复；脚本永远不会隐式选择最新版本。
- 正在运行的 WebPass 应用程序从不自动应用迁移。

## 验收标准

- A 连接的机器可以创建经过验证的离线套件，而无需提交生成的工件。
- A 使用 .NET 10 SDK 进行干净的离线源签出，并且传输的套件可以在没有 HTTP 包源的情况下生成 `WebPass.Migrations.exe`。
- 该捆绑包在集成测试中将每个提交的迁移应用到唯一的 SQL Server 数据库。
- 重新运行捆绑包构建可以安全地替换请求的输出。
- 英语和简体中文部署说明保持同步。
- 无 WebPass 运行时功能、安全行为、实体或迁移更改。
