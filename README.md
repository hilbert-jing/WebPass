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
- 已登录且启用的用户验证当前密码后，可以修改自己的登录密码。
- 30 分钟空闲会话期限、8 小时绝对上限和显式退出审计。
- AES-256-GCM 服务器密码加密，数据密钥由独立 RSA 证书封装。
- 当前密码二次验证、五分钟授权窗口和最长 30 秒密码显示。
- CSV/XLSX 内存导入、原子提交和密码即时加密。
- 不含密码的普通 CSV/XLSX 导出。
- 管理员专用、需要二次验证的明文密码 XLSX 导出。
- 登录、用户、权限、资产、网段、Ping、登录密码修改、密码查看、导入和导出审计。
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

生产环境必须使用独立的 HTTPS 证书和数据加密证书；生产值、证书权限和恢复要求统一见下方生产部署入口。

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

## 生产部署

唯一生产路径见 [DEPLOYMENT.md](DEPLOYMENT.md)。README 不再重复生产命令，以避免出现第二条部署路线。

## 安全与运维注意事项

- 普通用户权限由管理员逐用户分配；前端可见性不是安全边界，后端会再次检查权限。
- 登录 Cookie 使用 `Secure`、`HttpOnly` 和 `SameSite=Strict`。
- 登录会话空闲 30 分钟过期，即使持续操作也不能超过 8 小时。
- 当前密码二次验证授权有效五分钟，并绑定当前用户、会话和用户行版本。
- 密码明文只在授权响应和页面短时内存中出现，响应使用 `Cache-Control: no-store`。
- 管理员重置密码和用户自助改密都不会撤销已经签发的登录 Cookie；必要时应先禁用用户。
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
│  ├─ Initialize-WebPass.ps1
│  └─ Prepare-WebPassMigrationOfflineKit.ps1
├─ docs/
│  └─ superpowers/                 设计规格和实施计划
├─ DEPLOYMENT.md                   唯一生产部署手册
└─ WebPass.sln
```

## 详细文档

### 设计规格

- [局域网服务器资产管理系统设计](docs/superpowers/specs/2026-07-24-webpass-intranet-server-inventory-design.md)
- [安全导出与管理员密码导出设计](docs/superpowers/specs/2026-07-27-webpass-admin-password-export-design_ZH.md)
- [初始管理员工具设计](docs/superpowers/specs/2026-07-29-webpass-initial-administrator-tool-design_ZH.md)
- [用户管理、会话期限与 Migration Bundle 设计](docs/superpowers/specs/2026-07-29-webpass-user-session-migration-bundle-design.md)

## 当前范围

WebPass 第一版不包含：

- Active Directory 集成。
- IPv6。
- 自动资产发现和定时批量 Ping。
- 业务端口健康检查和公共 API。
- 移动端应用、微服务或容器编排。
- 应用内数据库备份或恢复。
- 强制首次改密、随机临时密码或邮件通知。
- 密码重置后立即撤销已有登录 Cookie。

