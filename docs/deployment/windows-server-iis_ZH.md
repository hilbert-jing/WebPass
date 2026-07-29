# Windows Server 和 IIS 上的 WebPass

本运行手册将 WebPass 部署为小型内部应用程序。它不会添加应用程序备份/还原功能，也不会将 SQL Server 暴露给局域网。

## 1. 先决条件和安装顺序

使用受支持的 64 位 Windows Server 及本地 SQL Server 2025 Express 实例。记录服务器 IPv4 地址，以及允许访问 WebPass 的精确局域网 CIDR。

按以下顺序安装：

1. 安装 IIS，并启用 Web Server 角色和 IIS Management Console。
2. 如果 Windows 要求，请重启。
3. 仅在 IIS 安装完成后再安装 .NET 10 Hosting Bundle。如果此前已安装，请在 IIS 就绪后修复 Hosting Bundle。
4. 确认 IIS Manager 在服务器模块下显示 `AspNetCoreModuleV2`。
5. 在本机安装 SQL Server Express。除非明确需要其他仅本地配置，否则保持 TCP/IP 禁用；不要为 SQL Server 创建局域网防火墙例外。

## 2. 证书

在 `LocalMachine\My` 中准备两张独立证书：

- HTTPS 服务器证书，其主题备用名称包含客户端使用的确切 IPv4 地址。
- 具有可导出私钥的 RSA 数据加密证书。不要复用 HTTPS 证书。

在启动 WebPass 前，请遵循 [certificates-and-key-recovery.md](certificates-and-key-recovery.md)。客户端证书警告代表部署失败，不应要求用户绕过。

## 3. 发布和数据库迁移

从已审核的源提交发布到暂存目录：

```powershell
dotnet publish src\WebPass.Web -c Release -r win-x64 --self-contained false -o C:\WebPass\staging
```

确认 `C:\WebPass\staging\web.config` 存在。将生产连接字符串配置为位于 `localhost` 或 `localhost\SQLEXPRESS` 的数据库；不要使用局域网主机名或远程地址。使用数据加密证书（而不是 HTTPS 证书）的指纹配置 `SecretEncryption:CertificateThumbprint`。

使用可修改 WebPass 数据库的部署身份应用 EF 迁移：

```powershell
dotnet ef database update --project src\WebPass.Web --startup-project src\WebPass.Web --configuration Release
```

运行时 IIS 应用程序池身份应仅获得所需的 WebPass 数据库访问权限。迁移后，不要保留运行时身份的架构所有者或服务器管理员权限。

## 4. 初始化 IIS

执行前审查脚本，然后预览其更改：

```powershell
.\scripts\Initialize-WebPass.ps1 `
  -PublishPath C:\WebPass\staging `
  -HttpsCertificateThumbprint '<HTTPS thumbprint>' `
  -DataEncryptionCertificateThumbprint '<data certificate thumbprint>' `
  -ListenAddress 10.20.30.40 `
  -LanRemoteAddress @('10.20.0.0/16') `
  -WhatIf
```

仅在拟创建的站点、应用程序池、证书绑定、ACL 和防火墙范围均正确后，才移除 `-WhatIf` 再次运行。该脚本会：

- 创建使用 `ApplicationPoolIdentity` 的专用 `WebPass` 应用程序池；
- 向该身份授予发布目录的读取/执行权限以及数据证书私钥的读取权限；
- 创建仅 HTTPS 的站点，并拒绝替换冲突的证书绑定；
- 创建或刷新仅限所提供局域网 CIDR 的 Domain/Private 入站防火墙规则。

该脚本特意不会启用 SQL 网络访问、创建数据库管理员、替换现有 TLS 绑定或安装先决条件。

## 5. 生产检查

在 IIS Manager 中确认：

- 站点只有一个 HTTPS 绑定，没有 HTTP 绑定。
- 应用程序池使用其专用虚拟身份，且该身份不属于管理员组。
- HTTPS 证书仍有效，且其 SAN 包含访问 IP。
- 数据证书私钥可由 `IIS AppPool\WebPass` 读取，普通用户组无法读取。
- 未启用详细 IIS 错误输出及请求/响应正文日志记录。

从受信任的局域网客户端确认浏览器报告证书受信任且没有警告。然后打开 `/health`；它必须仅返回 `application` 和 `database` 可用性。完成 [acceptance-test-record.md](acceptance-test-record.md)。

## 6. 更新和回滚

将每个已审核版本发布到新的带版本号目录。停止站点，切换 IIS 物理路径，启动站点并运行验收检查。如果应用程序失败，请停止站点并恢复先前物理路径。数据库回滚应由操作人员另行决定，不得临时依据导出的 XLSX 文件执行。
