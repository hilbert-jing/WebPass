# WebPass 生产部署

本文是 WebPass 唯一的生产部署手册。生产环境只采用以下路径：联网 Windows
准备机制作离线依赖包，离线 Windows 构建机生成同一提交的发布产物，最后把发布产物
传输到 Windows Server/IIS 服务器。不要在 IIS 服务器上从源码构建，也不要使用
README 或历史设计文档中的旧部署步骤。

## 环境要求

### 联网准备机

- Windows 11 或受支持的 64 位 Windows Server。
- Git 和 .NET 10 SDK。
- 能访问组织批准的 NuGet 源。
- 位于已审核提交、且 `git status --short` 无输出的 WebPass 源码检出。
- 用于向离线构建机传输文件的获准移动介质或内网文件共享。

### 离线构建机

- Windows 11 或受支持的 64 位 Windows Server。
- Git 和与项目匹配的 .NET 10 SDK。
- 位于离线包 `manifest.json` 所记录提交的干净 WebPass 源码检出。
- 从联网准备机传入的完整 `WebPassMigrationOfflineKit`。
- 不需要访问 HTTP NuGet 源；缺失依赖时必须停止构建。

### IIS/部署服务器

- 受支持的 64 位 Windows Server、固定 IPv4 地址和已批准的局域网 CIDR。
- 先安装 IIS Web Server 角色和 IIS Management Console，再安装 .NET 10
  Hosting Bundle；如果顺序相反，安装 IIS 后修复 Hosting Bundle。
- IIS Manager 的服务器模块中存在 `AspNetCoreModuleV2`。
- 本机 SQL Server 2025 Express，默认实例为 `localhost\SQLEXPRESS`。保持
  SQL Server TCP/IP 禁用，不创建面向局域网的 SQL Server 防火墙规则。
- Windows PowerShell 5.1 或更高版本，并可使用 `WebAdministration` 模块。
- `LocalMachine\My` 中有两张相互独立的证书：
  - HTTPS 证书：SAN 包含客户端实际访问的 IPv4 地址，且客户端信任其签发链；
  - 数据加密证书：RSA、包含私钥、允许导出，仅用于封装 WebPass 数据密钥。
- 数据加密证书已有加密 PFX 离线恢复副本，PFX 密码分开保存，并已在隔离设备上验证
  可以导入。只有获准管理员和 `IIS AppPool\WebPass` 可以读取其私钥。

### 准备数据加密证书恢复副本

组织没有提供专用 RSA 数据加密证书时，可在 IIS 服务器上创建一张。该证书不得用作
HTTPS 证书：

```powershell
$dataCert = New-SelfSignedCertificate `
  -Subject 'CN=WebPass Data Encryption' `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -KeyAlgorithm RSA `
  -KeyLength 3072 `
  -KeyExportPolicy Exportable `
  -KeyUsage KeyEncipherment,DataEncipherment `
  -NotAfter (Get-Date).AddYears(5)
$dataCert.Thumbprint

$pfxPassword = Read-Host 'PFX password' -AsSecureString
Export-PfxCertificate `
  -Cert "Cert:\LocalMachine\My\$($dataCert.Thumbprint)" `
  -FilePath 'D:\WebPass-Recovery\webpass-data-key.pfx' `
  -Password $pfxPassword `
  -CryptoAlgorithmOption AES256_SHA256
```

把 PFX 移到获准的离线加密介质并分开保存密码。继续前，在隔离设备上完成一次导入
测试并删除测试副本。

IIS 服务器不安装 .NET SDK 或 `dotnet-ef`，也不接收源码、离线依赖包、NuGet
缓存或本地 feed。

## 数据库准备

### 创建应用程序池身份

SQL Server 创建虚拟账号登录名前，先在 IIS 服务器创建应用程序池身份：

```powershell
Import-Module WebAdministration
if (-not (Test-Path -LiteralPath 'IIS:\AppPools\WebPass')) {
  New-WebAppPool -Name 'WebPass' | Out-Null
}
Set-ItemProperty -LiteralPath 'IIS:\AppPools\WebPass' `
  -Name managedRuntimeVersion -Value ''
Set-ItemProperty -LiteralPath 'IIS:\AppPools\WebPass' `
  -Name processModel.identityType -Value ApplicationPoolIdentity
```

在 IIS 服务器上创建一个专用 Windows 部署账号，例如
`CONTOSO\WebPassDeploy`。以下 SQL 由 SQL Server 管理员在 SSMS 中执行；把两个
`@DeploymentLogin` 值改成同一个实际 Windows 账号。运行时账号固定为
`IIS APPPOOL\WebPass`。

```sql
USE [master];
GO

IF DB_ID(N'WebPass') IS NULL
BEGIN
    CREATE DATABASE [WebPass];
END;
GO

DECLARE @DeploymentLogin sysname = N'CONTOSO\WebPassDeploy';

IF SUSER_ID(@DeploymentLogin) IS NULL
BEGIN
    DECLARE @CreateLogin nvarchar(max) =
        N'CREATE LOGIN ' + QUOTENAME(@DeploymentLogin) + N' FROM WINDOWS;';
    EXEC sys.sp_executesql @CreateLogin;
END;

IF SUSER_ID(N'IIS APPPOOL\WebPass') IS NULL
BEGIN
    CREATE LOGIN [IIS APPPOOL\WebPass] FROM WINDOWS;
END;
GO

USE [WebPass];
GO

DECLARE @DeploymentLogin sysname = N'CONTOSO\WebPassDeploy';

IF USER_ID(@DeploymentLogin) IS NULL
BEGIN
    DECLARE @CreateDeploymentUser nvarchar(max) =
        N'CREATE USER ' + QUOTENAME(@DeploymentLogin) +
        N' FOR LOGIN ' + QUOTENAME(@DeploymentLogin) + N';';
    EXEC sys.sp_executesql @CreateDeploymentUser;
END;

IF USER_ID(N'IIS APPPOOL\WebPass') IS NULL
BEGIN
    CREATE USER [IIS APPPOOL\WebPass]
        FOR LOGIN [IIS APPPOOL\WebPass];
END;

IF IS_ROLEMEMBER(N'db_owner', @DeploymentLogin) <> 1
BEGIN
    DECLARE @AddDeploymentOwner nvarchar(max) =
        N'ALTER ROLE [db_owner] ADD MEMBER ' +
        QUOTENAME(@DeploymentLogin) + N';';
    EXEC sys.sp_executesql @AddDeploymentOwner;
END;

IF IS_ROLEMEMBER(
    N'db_datareader',
    N'IIS APPPOOL\WebPass') <> 1
BEGIN
    ALTER ROLE [db_datareader]
        ADD MEMBER [IIS APPPOOL\WebPass];
END;

IF IS_ROLEMEMBER(
    N'db_datawriter',
    N'IIS APPPOOL\WebPass') <> 1
BEGIN
    ALTER ROLE [db_datawriter]
        ADD MEMBER [IIS APPPOOL\WebPass];
END;
GO
```

部署账号只在执行 migration bundle 和首次创建管理员期间保留 `db_owner`。网站
应用程序池身份不得属于 `db_owner`、`db_ddladmin`、`sysadmin` 或本机管理员组。

## 配置文件

每个发布版本使用独立的生产配置文件：

```text
C:\WebPass\releases\<releaseId>\site\appsettings.Production.json
```

文件内容如下。把证书指纹替换为数据加密证书的实际指纹，移除指纹中的空格。不要填入
HTTPS 证书指纹。

```json
{
  "ConnectionStrings": {
    "WebPass": "Server=localhost\\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True"
  },
  "WebPass": {
    "PingTimeoutMilliseconds": 1000,
    "PingMaxConcurrency": 2,
    "PingPerUserPerMinute": 5
  },
  "SecretEncryption": {
    "CertificateThumbprint": "0123456789ABCDEF0123456789ABCDEF01234567"
  }
}
```

确认 IIS 没有把 `ASPNETCORE_ENVIRONMENT` 覆盖为 `Development`；未设置时
ASP.NET Core 默认使用 `Production` 并加载此文件。连接字符串使用 Windows 集成
身份验证，不在文件中保存 SQL 密码。不要把生产配置、PFX、PFX 密码或私钥提交到
Git。`Initialize-WebPass.ps1` 会向应用程序池身份授予发布目录的读取和执行权限。

## 构建

### 1. 在联网准备机制作离线依赖包

从已审核的干净提交执行：

```powershell
git status --short
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath D:\WebPassTransfer\WebPassMigrationOfflineKit `
  -Force
```

`git status --short` 必须无输出。脚本成功后，将整个
`D:\WebPassTransfer\WebPassMigrationOfflineKit` 传输到离线构建机。该目录是
构建材料，不是部署产物，不得复制到 IIS 服务器。

### 2. 在离线构建机生成同一提交的全部产物

在干净源码检出中执行。以下命令只使用离线包的 `NuGet.Config`、`feed` 和
`packages`：

```powershell
$kit = 'E:\WebPassMigrationOfflineKit'
$manifest = Get-Content -LiteralPath "$kit\manifest.json" -Raw |
  ConvertFrom-Json
$head = (git rev-parse HEAD).Trim()
if ($manifest.sourceCommit -ne $head) {
  throw 'Offline kit and source commit do not match.'
}
if (git status --short) {
  throw 'The offline build source tree is not clean.'
}

$releaseId = (git rev-parse --short=12 HEAD).Trim()
$release = "C:\WebPass\out\$releaseId"
if (Test-Path -LiteralPath $release) {
  throw "Release output already exists: $release"
}

dotnet restore WebPass.sln `
  --runtime win-x64 `
  --configfile "$kit\NuGet.Config" `
  --packages "$kit\packages" `
  --no-http-cache `
  -p:RestoreSources="$kit\feed" `
  -p:RestoreFallbackFolders= `
  -p:RestoreAdditionalProjectFallbackFolders= `
  -p:DisableImplicitNuGetFallbackFolder=true `
  -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
  throw 'Offline solution restore failed.'
}

dotnet publish src\WebPass.Web\WebPass.Web.csproj `
  -c Release -r win-x64 --self-contained false --no-restore `
  -o "$release\site"
if ($LASTEXITCODE -ne 0) {
  throw 'Website publish failed.'
}

dotnet publish src\WebPass.AdminInit\WebPass.AdminInit.csproj `
  -c Release -r win-x64 --self-contained false --no-restore `
  -o "$release\admin"
if ($LASTEXITCODE -ne 0) {
  throw 'Administrator utility publish failed.'
}

.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath $kit `
  -OutputPath "$release\WebPass.Migrations.exe"

Copy-Item -LiteralPath '.\scripts\Initialize-WebPass.ps1' `
  -Destination "$release\Initialize-WebPass.ps1"

foreach ($required in @(
  "$release\site\web.config",
  "$release\admin\WebPass.AdminInit.exe",
  "$release\WebPass.Migrations.exe",
  "$release\Initialize-WebPass.ps1")) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
    throw "Missing release artifact: $required"
  }
}

Get-FileHash -Algorithm SHA256 `
  "$release\admin\WebPass.AdminInit.exe", `
  "$release\WebPass.Migrations.exe", `
  "$release\Initialize-WebPass.ps1"
```

记录发布目录名、完整 Git 提交和 SHA-256 输出。每个已审核发布都重新生成 migration
bundle；不得复用其他提交的 bundle。只传输整个 `$release` 目录，不传输离线依赖包、
源码、`.tools`、NuGet 缓存或本地 feed。

## 部署

以下命令在 IIS 服务器以获准部署账号运行。把离线构建机的整个发布目录复制到
`C:\WebPass\releases\<releaseId>`，并设置本次路径：

```powershell
$releaseId = 'abcdef123456'
$release = "C:\WebPass\releases\$releaseId"
$connection = 'Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True'

foreach ($required in @(
  "$release\site\web.config",
  "$release\admin\WebPass.AdminInit.exe",
  "$release\WebPass.Migrations.exe",
  "$release\Initialize-WebPass.ps1")) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
    throw "Missing transferred artifact: $required"
  }
}
```

按照“配置文件”章节创建
`$release\site\appsettings.Production.json`，并用
`ConvertFrom-Json` 验证 JSON 可解析：

```powershell
Get-Content -LiteralPath `
  "$release\site\appsettings.Production.json" -Raw |
  ConvertFrom-Json | Out-Null
```

### 1. 停止现有站点并备份数据库

首次部署时站点尚不存在，可以跳过停止命令。更新时先停止站点：

```powershell
Import-Module WebAdministration
if (Test-Path -LiteralPath 'IIS:\Sites\WebPass') {
  Stop-Website -Name 'WebPass'
}
```

在 SSMS 中设置本次备份文件的准确路径，执行完整备份并立即验证：

```sql
DECLARE @BackupPath nvarchar(260) =
    N'D:\WebPassBackups\WebPass-before-abcdef123456.bak';

BACKUP DATABASE [WebPass]
TO DISK = @BackupPath
WITH INIT, CHECKSUM, STATS = 10;

RESTORE VERIFYONLY
FROM DISK = @BackupPath
WITH CHECKSUM;
```

备份或 `RESTORE VERIFYONLY` 失败时停止部署，不执行迁移。

### 2. 执行数据库迁移

```powershell
& "$release\WebPass.Migrations.exe" --connection $connection
if ($LASTEXITCODE -ne 0) {
  throw 'Database migration failed; keep the site stopped.'
}
```

网站本身不会自动应用迁移。bundle 执行失败时不得切换目录或启动 IIS。

### 3. 首次部署时创建初始管理员

只在数据库中尚无管理员的首次部署执行：

```powershell
& "$release\admin\WebPass.AdminInit.exe" `
  --connection-string $connection `
  --username admin
if ($LASTEXITCODE -ne 0) {
  throw 'Initial administrator creation failed.'
}
Remove-Item -LiteralPath "$release\admin" -Recurse -Force
```

密码通过隐藏的交互提示输入。工具不会覆盖已有账号；更新部署不要再次运行它。
更新部署完成产物检查后，直接删除未使用的管理员工具：

```powershell
Remove-Item -LiteralPath "$release\admin" -Recurse -Force
```

### 4. 撤销部署账号的数据库所有者权限

在 SSMS 中把账号改为“数据库准备”时使用的实际账号后执行：

```sql
USE [WebPass];
GO

DECLARE @DeploymentLogin sysname = N'CONTOSO\WebPassDeploy';

IF IS_ROLEMEMBER(N'db_owner', @DeploymentLogin) = 1
BEGIN
    DECLARE @DropDeploymentOwner nvarchar(max) =
        N'ALTER ROLE [db_owner] DROP MEMBER ' +
        QUOTENAME(@DeploymentLogin) + N';';
    EXEC sys.sp_executesql @DropDeploymentOwner;
END;
GO
```

## 启动

### 首次部署

先预览初始化脚本的站点、应用程序池、证书 ACL、HTTPS 绑定和防火墙更改：

```powershell
$httpsThumbprint = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
$dataThumbprint = 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
$listenAddress = '10.20.30.40'
$lanCidrs = @('10.20.0.0/16')

& "$release\Initialize-WebPass.ps1" `
  -PublishPath "$release\site" `
  -HttpsCertificateThumbprint $httpsThumbprint `
  -DataEncryptionCertificateThumbprint $dataThumbprint `
  -ListenAddress $listenAddress `
  -LanRemoteAddress $lanCidrs `
  -WhatIf
```

核对输出无误后，原样再次执行并移除 `-WhatIf`。随后显式启动：

```powershell
Start-WebAppPool -Name 'WebPass'
Start-Website -Name 'WebPass'
```

### 更新部署

先用本次发布脚本为新目录应用 ACL，并复核现有 HTTPS 绑定和防火墙范围：

```powershell
& "$release\Initialize-WebPass.ps1" `
  -PublishPath "$release\site" `
  -HttpsCertificateThumbprint $httpsThumbprint `
  -DataEncryptionCertificateThumbprint $dataThumbprint `
  -ListenAddress $listenAddress `
  -LanRemoteAddress $lanCidrs `
  -WhatIf

& "$release\Initialize-WebPass.ps1" `
  -PublishPath "$release\site" `
  -HttpsCertificateThumbprint $httpsThumbprint `
  -DataEncryptionCertificateThumbprint $dataThumbprint `
  -ListenAddress $listenAddress `
  -LanRemoteAddress $lanCidrs
```

然后切换物理路径并启动：

```powershell
Stop-Website -Name 'WebPass'
Set-ItemProperty -LiteralPath 'IIS:\Sites\WebPass' `
  -Name physicalPath -Value "$release\site"
Start-WebAppPool -Name 'WebPass'
Start-Website -Name 'WebPass'
```

不要覆盖或删除上一版本目录，至少保留到本次发布完成验收和备份保留期结束。

## 验证

在 IIS 服务器上完成以下检查：

```powershell
Import-Module WebAdministration

$site = Get-Item -LiteralPath 'IIS:\Sites\WebPass'
$pool = Get-Item -LiteralPath 'IIS:\AppPools\WebPass'
$bindings = @(Get-WebBinding -Name 'WebPass')
$firewall = Get-NetFirewallRule `
  -DisplayName 'WebPass HTTPS - LAN only'
$remoteScope = $firewall | Get-NetFirewallAddressFilter

$site.state
$site.physicalPath
$pool.state
$bindings | Select-Object protocol,bindingInformation
$remoteScope.RemoteAddress

$health = Invoke-RestMethod `
  -Uri "https://$listenAddress/health"
if ($health.application -ne 'available' -or
    $health.database -ne 'available') {
  throw 'WebPass health verification failed.'
}
```

必须确认：

- 站点物理路径是本次版本化 `site` 目录，应用程序池和站点状态均为 `Started`；
- 只有预期 HTTPS 绑定，没有 HTTP 绑定，HTTPS 证书未过期且 SAN 包含访问地址；
- `IIS AppPool\WebPass` 可以读取数据证书私钥，普通用户组不能读取；
- Windows 防火墙只允许批准的局域网 CIDR 访问 TCP 443；
- SQL Server TCP/IP 保持禁用，局域网客户端无法连接 SQL Server；
- `/health` 只返回 `application` 和 `database` 可用性，不含版本、路径、异常或堆栈；
- 部署账号已不属于 `db_owner`，应用程序池账号只属于 `db_datareader` 和
  `db_datawriter`；
- 未认证访问跳转到 HTTPS 登录页，管理员能够登录；
- 普通用户权限、密码二次验证、密码显示、导入、普通导出、管理员密码导出和审计
  符合预期；普通导出不含密码，加密字段和敏感响应使用 `no-store`；
- 从获准局域网客户端访问时浏览器没有任何证书警告；
- 重启 Windows Server 后，SQL Server、IIS、WebPass 应用程序池和 `/health`
  自动恢复。

任何一项失败都视为部署失败，进入“回滚”章节，不要求用户绕过证书、安全或权限错误。

## 回滚

### 应用版本回滚

记录上一个已验收版本的目录，停止站点、切回旧目录并启动：

```powershell
$previousRelease = 'C:\WebPass\releases\123456789abc\site'

Stop-Website -Name 'WebPass'
Set-ItemProperty -LiteralPath 'IIS:\Sites\WebPass' `
  -Name physicalPath -Value $previousRelease
Start-WebAppPool -Name 'WebPass'
Start-Website -Name 'WebPass'
```

重新执行“验证”章节。不要删除失败版本及其日志，直到问题调查完成。

### 数据库恢复边界

不得临时编写反向 SQL 或把 migration bundle 当作降级工具。如果新迁移与旧应用不
兼容，保持站点停止，由 SQL Server 管理员恢复部署前已验证的完整备份，然后再启动与
该备份匹配的旧应用版本：

```sql
USE [master];
GO

ALTER DATABASE [WebPass]
SET SINGLE_USER WITH ROLLBACK IMMEDIATE;

RESTORE DATABASE [WebPass]
FROM DISK = N'D:\WebPassBackups\WebPass-before-abcdef123456.bak'
WITH REPLACE, RECOVERY, CHECKSUM, STATS = 10;

ALTER DATABASE [WebPass] SET MULTI_USER;
GO
```

恢复后再次确认运行时数据库角色、`/health`、登录和密码解密。CSV/XLSX 导出不是
数据库备份，不能用于回滚。

### 数据加密证书恢复

如果服务器更换或证书丢失，保持站点停止，把匹配指纹的加密 PFX 导入新服务器的
`LocalMachine\My`，确认具有 RSA 私钥，再使用本发布版本的
`Initialize-WebPass.ps1` 恢复 `IIS AppPool\WebPass` 的私钥读取权限。新建一张
证书不能解封旧数据密钥；如果原私钥和 PFX 恢复副本同时丢失，现有加密密码无法恢复。

