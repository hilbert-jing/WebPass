# WebPass

WebPass 是面向 Windows Server 局域网环境的服务器资产与密码管理系统。它使用
ASP.NET Core Razor Pages、Entity Framework Core 和 SQL Server Express，
适合少量内部用户维护数百台服务器资产、IPv4 网段、存活状态和受保护的服务器密码。

系统不依赖外部互联网服务。生产环境通过 IIS 提供 HTTPS，业务数据保存在本机
SQL Server，服务器密码使用 Windows 本机证书保护的数据密钥进行加密。

## 核心功能

- 服务器资产登记、编辑、归档、重新登记、搜索、筛选和分页。
- 按 IPv4 数值排序，并根据 CIDR 动态展示已登记和空闲地址。
- 管理 IPv4 网段，拒绝重叠网段、网络地址和广播地址。
- 后端按需执行 Ping，保存检测结果，但不自动改变人工存活状态。
- 应用本地账户、Argon2id 登录密码哈希、登录锁定和逐用户权限。
- 管理员创建、启用、禁用普通用户，重置密码并分配模块权限。
- 30 分钟空闲会话期限、8 小时绝对上限和显式退出审计。
- AES-256-GCM 服务器密码加密，数据密钥由独立 RSA 证书封装。
- 当前密码二次验证、五分钟授权窗口和最长 30 秒密码显示。
- CSV/XLSX 内存导入、原子提交和密码即时加密。
- 不含密码的普通 CSV/XLSX 导出。
- 管理员专用、需要二次验证的明文密码 XLSX 导出。
- 登录、用户、权限、资产、网段、Ping、密码查看、导入和导出审计。
- HTTPS 重定向、CSRF、防公式注入、CSP、安全响应头和敏感接口限流。

## 架构

```text
局域网浏览器
    │ HTTPS
    ▼
Windows Firewall → IIS
                      │
                      ▼
                 WebPass.Web
                 ├─ Razor Pages
                 ├─ 登录与逐用户授权
                 ├─ 资产、网段与 Ping
                 ├─ 密码加解密
                 ├─ 导入与导出
                 └─ 审计
                      │
             ┌────────┴────────┐
             ▼                 ▼
   SQL Server 2025 Express   LocalMachine\My
                            数据加密证书
```

主要组件：

| 组件 | 用途 |
|---|---|
| `WebPass.Web` | Web 应用和领域、数据、基础设施代码 |
| `WebPass.AdminInit` | 交互式创建初始管理员 |
| `WebPass.Migrations.exe` | 发布时生成的 EF Core migration bundle |
| `WebPass.UnitTests` | 单元测试 |
| `WebPass.IntegrationTests` | Web、EF Core 和 SQL Server 集成测试 |

## 技术栈

- .NET 10 / ASP.NET Core 10 Razor Pages
- Entity Framework Core 10
- SQL Server 2025 Express
- ClosedXML
- Argon2id
- AES-256-GCM 和 Windows RSA 证书
- IIS 与 ASP.NET Core Module V2

## 开发环境

建议使用：

- Windows 11 或受支持的 64 位 Windows Server
- .NET 10 SDK
- SQL Server 2025 Express，实例名默认为 `localhost\SQLEXPRESS`
- PowerShell
- 用于完整密码流程的数据加密证书

克隆后还原依赖：

```powershell
dotnet restore WebPass.sln
```

migration bundle 构建脚本使用仓库本地 EF 工具。首次准备开发环境时安装与项目匹配的
EF Core 工具：

```powershell
dotnet tool install dotnet-ef --version 10.0.0 --tool-path .tools
```

`.tools`、`bin` 和 `obj` 均被 Git 忽略。

### 内网离线部署与 EF 工具准备

.NET 10 SDK 不包含 `dotnet-ef`。生成 migration bundle 的开发或构建电脑需要
额外安装与项目匹配的 `dotnet-ef` 10.0.0；已经生成 bundle 的测试服务器不需要
安装 EF Core CLI，也不需要保留项目源码。

推荐在能够访问 NuGet 或已准备完整离线 NuGet 源的开发/构建电脑生成以下产物：

- `WebPass.Web` 的 `win-x64` 发布目录。
- `WebPass.AdminInit` 的 `win-x64` 发布目录。
- 与同一源代码版本对应的 `WebPass.Migrations.exe`。

将这些产物复制到内网测试服务器后，服务器直接执行
`WebPass.Migrations.exe --connection "..."`。如果 bundle 构建或执行失败，应停止部署，不得继续切换或启动 IIS。

如果选择在内网服务器从源码构建，则除了 .NET 10 SDK 和 SQL Server 外，还必须
离线准备 `dotnet-ef` 10.0.0、项目全部 NuGet 包及其传递依赖，以及可用的本地
NuGet 源或已还原缓存。该方式准备内容更多，不是推荐部署路径。

IIS 服务器还必须安装 .NET 10 Hosting Bundle。SDK 不能替代 Hosting Bundle 中的
ASP.NET Core Module V2；应先安装 IIS，再安装或修复 Hosting Bundle。只要发布产物
已在构建电脑生成，测试服务器通常无需安装 SDK。

## 配置

默认配置位于 `src/WebPass.Web/appsettings.json`：

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
    "CertificateThumbprint": ""
  }
}
```

不要把生产连接字符串、证书私钥或其他敏感值提交到仓库。开发时可以使用环境变量覆盖：

```powershell
$env:ConnectionStrings__WebPass = 'Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True'
$env:SecretEncryption__CertificateThumbprint = '<data-encryption-certificate-thumbprint>'
```

HTTPS 证书和数据加密证书必须分开。数据加密证书的创建、权限和离线 PFX 恢复副本要求见
[证书与密钥恢复说明](docs/deployment/certificates-and-key-recovery_ZH.md)。

## 初始化本地数据库

应用迁移：

```powershell
.\.tools\dotnet-ef.exe database update `
  --project src\WebPass.Web `
  --startup-project src\WebPass.Web
```

创建初始管理员：

```powershell
dotnet run --project src\WebPass.AdminInit -- `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

工具通过隐藏输入读取并确认密码。每次成功执行都会创建一个不同用户名的管理员；它不会
覆盖已有账号。

## 本地运行

准备并信任 ASP.NET Core 开发证书：

```powershell
dotnet dev-certs https --trust
```

使用 HTTPS 启动应用：

```powershell
$env:ASPNETCORE_URLS = 'https://localhost:5001'
dotnet run --project src\WebPass.Web
```

访问 `https://localhost:5001/login`。完整的服务器密码写入、查看和导出流程需要有效的
数据加密证书及其私钥访问权限。

## 构建与测试

构建解决方案：

```powershell
dotnet build WebPass.sln -c Release
```

运行完整测试：

```powershell
dotnet test WebPass.sln -c Release
```

发布前检查 EF 模型与迁移是否一致：

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web `
  --startup-project src\WebPass.Web `
  --configuration Release
```

完整集成测试需要本机 SQL Server Express。发布前还应执行：

```powershell
git diff --check
```

## Windows Server 发布摘要

以下内容只作为流程入口。正式部署必须遵循
[Windows Server 与 IIS 部署手册](docs/deployment/windows-server-iis_ZH.md)。

### 1. 准备服务器

按顺序安装：

1. IIS。
2. .NET 10 Hosting Bundle；如果先于 IIS 安装，安装 IIS 后修复 Hosting Bundle。
3. 本机 SQL Server 2025 Express。
4. HTTPS 证书和独立的数据加密证书。

SQL Server 不应向局域网开放。生产网站使用独立、低权限的 IIS 应用池身份。

### 2. 发布网站

```powershell
dotnet publish src\WebPass.Web -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\staging
```

确认输出中存在 `web.config`。

### 3. 生成并执行 migration bundle

bundle 必须从与网站相同的审核源代码版本生成：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

使用具备数据库结构修改权限的部署身份执行：

```powershell
C:\WebPass\staging\WebPass.Migrations.exe `
  --connection "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True"
```

bundle 生成或执行失败时必须停止部署，不得继续切换或启动 IIS。运行中的网站不会自动
应用迁移，IIS 应用池身份也不应保留数据库结构修改权限。

### 4. 初始化 IIS

先预览脚本操作：

```powershell
.\scripts\Initialize-WebPass.ps1 `
  -PublishPath C:\WebPass\staging `
  -HttpsCertificateThumbprint '<HTTPS thumbprint>' `
  -DataEncryptionCertificateThumbprint '<data certificate thumbprint>' `
  -ListenAddress 10.20.30.40 `
  -LanRemoteAddress @('10.20.0.0/16') `
  -WhatIf
```

核对站点、应用池、证书、ACL 和防火墙范围后，再移除 `-WhatIf` 执行。

### 5. 创建管理员

```powershell
dotnet publish src\WebPass.AdminInit -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\AdminInit

C:\WebPass\AdminInit\WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

管理员创建完成后，可以删除服务器上的 `C:\WebPass\AdminInit` 目录。

### 6. 验收

必须从受信任的局域网客户端验证：

- 浏览器没有任何证书警告。
- Windows 防火墙只允许批准的局域网 CIDR。
- IIS 只有 HTTPS 绑定。
- SQL Server 不能从局域网客户端访问。
- `/health` 只返回应用和数据库可用性。
- 登录、权限、密码查看、导入、导出和审计符合预期。
- Windows Server 重启后 SQL Server、IIS 和应用自动恢复。

使用[生产验收记录](docs/deployment/acceptance-test-record_ZH.md)保存证据。

## 安全与运维注意事项

- 普通用户权限由管理员逐用户分配；前端可见性不是安全边界，后端会再次检查权限。
- 登录 Cookie 使用 `Secure`、`HttpOnly` 和 `SameSite=Strict`。
- 登录会话空闲 30 分钟过期，即使持续操作也不能超过 8 小时。
- 当前密码二次验证授权有效五分钟，并绑定当前用户、会话和用户行版本。
- 密码明文只在授权响应和页面短时内存中出现，响应使用 `Cache-Control: no-store`。
- 密码重置不会撤销目标用户已经签发的登录 Cookie；必要时应先禁用用户。
- 普通导出不包含密码、密文、密钥、登录哈希、会话或审计信息。
- 管理员密码导出包含明文密码，只支持 XLSX，下载文件必须按敏感凭据处理。
- 数据加密证书私钥及其离线 PFX 恢复副本是恢复服务器密码的必要条件。
- 更新前备份数据库，并保留上一版本发布目录；不要把导出的 XLSX 当作数据库备份。
- 发布产物和 migration bundle 应来自同一审核提交。bundle 二进制不提交 Git。

## 项目结构

```text
WebPass/
├─ src/
│  ├─ WebPass.Web/                 Web 应用、领域模型、数据访问和基础设施
│  └─ WebPass.AdminInit/           初始管理员创建工具
├─ tests/
│  ├─ WebPass.UnitTests/           单元测试
│  └─ WebPass.IntegrationTests/    Web、EF Core 和 SQL Server 集成测试
├─ scripts/
│  ├─ Build-WebPassMigrationBundle.ps1
│  └─ Initialize-WebPass.ps1
├─ docs/
│  ├─ deployment/                  部署、证书恢复和验收文档
│  └─ superpowers/                 设计规格和实施计划
└─ WebPass.sln
```

## 详细文档

### 设计规格

- [局域网服务器资产管理系统设计](docs/superpowers/specs/2026-07-24-webpass-intranet-server-inventory-design.md)
- [安全导出与管理员密码导出设计](docs/superpowers/specs/2026-07-27-webpass-admin-password-export-design_ZH.md)
- [初始管理员工具设计](docs/superpowers/specs/2026-07-29-webpass-initial-administrator-tool-design_ZH.md)
- [用户管理、会话期限与 Migration Bundle 设计](docs/superpowers/specs/2026-07-29-webpass-user-session-migration-bundle-design.md)

### 部署与运维

- [Windows Server 与 IIS 部署](docs/deployment/windows-server-iis_ZH.md)
- [证书与数据密钥恢复](docs/deployment/certificates-and-key-recovery_ZH.md)
- [生产验收记录](docs/deployment/acceptance-test-record_ZH.md)

## 当前范围

WebPass 第一版不包含：

- Active Directory 集成。
- IPv6。
- 自动资产发现和定时批量 Ping。
- 业务端口健康检查和公共 API。
- 移动端应用、微服务或容器编排。
- 应用内数据库备份或恢复。
- 强制首次改密、自助改密、随机临时密码或邮件通知。
- 密码重置后立即撤销已有登录 Cookie。

