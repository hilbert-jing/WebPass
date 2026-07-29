# 证书和数据密钥恢复

WebPass 使用两张相互独立的证书。HTTPS 证书用于保护 IIS 上的浏览器传输；数据加密证书用于封装 AES 数据密钥，绝不可作为 IIS TLS 证书使用。

## 创建或导入数据加密证书

如果可用，请使用组织颁发的 RSA 证书。对于本地管理的内部部署，管理员可在 `LocalMachine\My` 中创建专用 RSA 证书：

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
```

将输出的指纹配置为 `SecretEncryption:CertificateThumbprint`。仅向专用 IIS 应用程序池身份授予私钥读取权限；`Initialize-WebPass.ps1` 会执行该 ACL 配置步骤。

## 离线加密 PFX 恢复副本

解密现有服务器密码需要数据加密证书的私钥。证书配置完成后，立即导出一份加密 PFX 恢复副本：

```powershell
$pfxPassword = Read-Host 'PFX password' -AsSecureString
Export-PfxCertificate `
  -Cert "Cert:\LocalMachine\My\<data certificate thumbprint>" `
  -FilePath 'D:\WebPass-Recovery\webpass-data-key.pfx' `
  -Password $pfxPassword `
  -CryptoAlgorithmOption AES256_SHA256
```

将 PFX 移至已批准的离线加密介质，并单独保存其密码。请勿将 PFX、密码或私钥材料放入应用程序目录、源代码仓库、普通 XLSX/CSV 导出、日志或审计记录中。在声明恢复副本可用之前，请在隔离设备上测试它。

## 证书丢失后的恢复

1. 停止 WebPass IIS 站点。不要创建替代数据证书后让应用程序使用它；新的私钥无法解封现有数据密钥。
2. 在替换服务器上，将加密恢复 PFX 导入 `LocalMachine\My`。
3. 确认导入证书的指纹与配置的指纹完全一致，且证书具有 RSA 私钥。
4. 使用初始化脚本或证书管理器，向 `IIS AppPool\WebPass` 授予该私钥的读取权限。
5. 启动站点并验证 `/health`。
6. 使用获授权的测试账户重新验证并显示一项已知的非生产测试凭据。确认显示审计记录只包含元数据而不含密码。

如果匹配的私钥及其恢复副本均丢失，WebPass 无法恢复现有加密密码。在继续更改任何机密信息前，请恢复正确的证书材料。

## HTTPS 信任验证

在每台已批准客户端的“受信任的根证书颁发机构”存储中安装内部根证书。浏览证书 SAN 中使用的确切 HTTPS IP 地址。如果浏览器报告未知颁发者、名称/IP 不匹配、过期或任何其他证书警告，请停止验收。
