# WebPass on Windows Server and IIS

This runbook deploys WebPass as a small internal application. It does not add an application backup/restore feature and it does not expose SQL Server to the LAN.

## 1. Prerequisites and installation order

Use a supported 64-bit Windows Server and a local SQL Server 2025 Express instance. Record the server IPv4 address and the exact LAN CIDRs that may reach WebPass.

Install in this order:

1. Install IIS with the Web Server role and IIS Management Console.
2. Restart if Windows requests it.
3. Install the .NET 10 Hosting Bundle only after IIS. If it was installed earlier, repair the Hosting Bundle after IIS is present.
4. Confirm IIS Manager shows `AspNetCoreModuleV2` under server modules.
5. Install SQL Server Express locally. Leave TCP/IP disabled unless another local-only configuration is explicitly required; do not create a LAN firewall exception for SQL Server.

## 2. Certificates

Prepare two separate certificates in `LocalMachine\My`:

- An HTTPS server certificate whose subject alternative names include the exact IPv4 address used by clients.
- An RSA data-encryption certificate with an exportable private key. Do not reuse the HTTPS certificate.

Follow [certificates-and-key-recovery.md](certificates-and-key-recovery.md) before starting WebPass. Client certificate warnings are a deployment failure, not a condition users should bypass.

## 3. Publish and database migration

From the reviewed source commit, publish to a staging directory:

```powershell
dotnet publish src\WebPass.Web -c Release -r win-x64 --self-contained false -o C:\WebPass\staging
```

Confirm `C:\WebPass\staging\web.config` exists. Configure the production connection string for a database on `localhost` or `localhost\SQLEXPRESS`; do not use a LAN hostname or remote address. Configure `SecretEncryption:CertificateThumbprint` with the data-encryption certificate thumbprint, not the HTTPS certificate thumbprint.

Build the migration bundle from the same reviewed source commit and place it
in the staging directory:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

Apply migrations using a deployment identity that can alter the WebPass
database:

```powershell
C:\WebPass\staging\WebPass.Migrations.exe `
  --connection "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True"
```

Stop deployment if bundle creation or execution fails. Generate a new bundle
for every reviewed release; do not reuse a bundle from another source
version. The running WebPass website does not apply migrations automatically.

The runtime IIS application-pool identity should receive only the WebPass database access it needs. Do not leave schema-owner or server-administrator rights on the runtime identity after migration.

## 4. Initialize IIS

Review the script before execution, then preview its changes:

```powershell
.\scripts\Initialize-WebPass.ps1 `
  -PublishPath C:\WebPass\staging `
  -HttpsCertificateThumbprint '<HTTPS thumbprint>' `
  -DataEncryptionCertificateThumbprint '<data certificate thumbprint>' `
  -ListenAddress 10.20.30.40 `
  -LanRemoteAddress @('10.20.0.0/16') `
  -WhatIf
```

Run it again without `-WhatIf` only after the proposed site, application pool, certificate binding, ACL, and firewall scope are correct. The script:

- creates a dedicated `WebPass` application pool using `ApplicationPoolIdentity`;
- grants that identity read/execute access to the publish directory and read access to the data certificate private key;
- creates an HTTPS-only site and refuses to replace a conflicting certificate binding;
- creates or refreshes a Domain/Private inbound firewall rule restricted to the supplied LAN CIDRs.

The script deliberately does not enable SQL network access, create database administrators, replace existing TLS bindings, or install prerequisites.

## 5. Create an administrator

Publish the local initialization utility separately:

```powershell
dotnet publish src\WebPass.AdminInit -c Release -r win-x64 `
  --self-contained false -o C:\WebPass\AdminInit
```

Run it locally with a deployment identity that can insert into the WebPass
database:

```powershell
C:\WebPass\AdminInit\WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

Enter and confirm the password at the hidden prompts. The command does not
check whether users or administrators already exist; every successful
invocation creates another administrator with the requested distinct
username.

The utility is not required by the running website. Delete
`C:\WebPass\AdminInit` after use if operators do not need to retain it.

## 6. Production checks

In IIS Manager verify:

- The site has one HTTPS binding and no HTTP binding.
- The application pool uses its dedicated virtual identity and has no administrator membership.
- The HTTPS certificate is current and contains the access IP in its SAN.
- The data certificate private key is readable by `IIS AppPool\WebPass` and not by general user groups.
- Detailed IIS error output and request/response body logging are not enabled.

From a trusted LAN client, verify the browser reports a trusted certificate with no warning. Then open `/health`; it must return only `application` and `database` availability. Complete [acceptance-test-record.md](acceptance-test-record.md).

## 7. Update and rollback

Publish each reviewed release to a new versioned directory. Stop the site, switch the IIS physical path, start the site, and run the acceptance checks. If the application fails, stop the site and restore the previous physical path. Database rollback is a separate operator decision and must not be improvised from an exported XLSX file.
