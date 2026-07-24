# WebPass Secure Data Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add server-password encryption and reveal, secure import/export, administrator backup/recovery, and IIS production hardening to the completed core platform.

**Architecture:** The application remains a single ASP.NET Core 10 application. A versioned AES data key is wrapped by a Windows certificate; password reveal needs a server-side reauthentication grant; imported passwords are encrypted before preview state is retained. IIS is the HTTPS boundary and SQL Server stays local to the server.

**Tech Stack:** .NET 10, ASP.NET Core, X.509 Windows Certificate Store, AES-GCM, Argon2id, SQL Server 2025 Express, ClosedXML, xUnit.

## Global Constraints

- Complete `2026-07-24-webpass-core-platform.md` first.
- Passwords must never appear in logs, audits, ordinary exports or disk-backed import staging.
- HTTPS and data-encryption certificates are separate.
- Password reveal requires `SecretReveal` permission, current-password reauthentication, a 5-minute grant and 30-second browser display.
- Import supports IPv4 `.xlsx` and `.csv` only, up to 10 MB and 5,000 rows, with atomic commit.
- Ordinary exports contain no passwords or cryptographic material.
- Backup and restore are administrator-only.
- Each task starts with a failing test and ends with a focused commit.

---

## File Structure

- `src/WebPass.Web/Application/Secrets/`: encryption, reauthentication and reveal contracts.
- `src/WebPass.Web/Infrastructure/Secrets/`: Windows certificate access and AES-GCM.
- `src/WebPass.Web/Application/Importing/`, `Exporting/`, `Backups/`: file and recovery use cases.
- `src/WebPass.Web/Pages/Secrets/`, `Imports/`, `Exports/`, `Admin/`: protected handlers.
- `docs/deployment/`: IIS, certificate and recovery instructions.

### Task 1: Add certificate-backed envelope encryption and key rotation

**Files:**
- Create: `src/WebPass.Web/Domain/Entities/DataEncryptionKey.cs`
- Create: `src/WebPass.Web/Domain/Entities/ServerSecret.cs`
- Create: `src/WebPass.Web/Application/Secrets/ISecretCipher.cs`
- Create: `src/WebPass.Web/Application/Secrets/SecretEnvelope.cs`
- Create: `src/WebPass.Web/Infrastructure/Secrets/CertificateKeyWrapper.cs`
- Create: `src/WebPass.Web/Infrastructure/Secrets/AesGcmSecretCipher.cs`
- Create: `src/WebPass.Web/Data/Migrations/202607240002_AddSecrets.cs`
- Test: `tests/WebPass.UnitTests/Secrets/AesGcmSecretCipherTests.cs`

**Interfaces:**
- Produces `Task<SecretEnvelope> EncryptAsync(string plaintext, CancellationToken ct)`.
- Produces `Task<string> DecryptAsync(ServerSecret secret, CancellationToken ct)`.
- Produces `Task<int> RotateAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Encrypt_then_decrypt_returns_original_value()
{
    var encrypted = await cipher.EncryptAsync("S3cret!", default);
    Assert.Equal("S3cret!", await cipher.DecryptAsync(encrypted.ToServerSecret(), default));
}
```

- [ ] **Step 2: Run it to verify failure**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~AesGcmSecretCipherTests`

Expected: FAIL because `ISecretCipher` does not exist.

- [ ] **Step 3: Implement the cipher and key store**

```csharp
public sealed record SecretEnvelope(byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion);
```

Generate a random 32-byte AES key for each active key version; wrap it with the configured X.509 public key and store only the wrapped key, certificate thumbprint and lifecycle dates. Use a 12-byte random nonce for every AES-GCM operation. Load the certificate by thumbprint from `LocalMachine\\My`; if the private key is unreadable, reject secret operations.

- [ ] **Step 4: Run tests and migration**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~AesGcmSecretCipherTests`

Expected: PASS.

Run: `dotnet ef database update --project src/WebPass.Web --startup-project src/WebPass.Web`

Expected: secret tables are created.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Domain/Entities src/WebPass.Web/Application/Secrets src/WebPass.Web/Infrastructure/Secrets src/WebPass.Web/Data tests
git commit -m "feat: add certificate-backed secret encryption"
```

### Task 2: Add reauthentication and short-lived password reveal

**Files:**
- Create: `src/WebPass.Web/Application/Secrets/ReauthenticationService.cs`
- Create: `src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml.cs`
- Create: `src/WebPass.Web/Pages/Secrets/Reveal.cshtml.cs`
- Create: `src/WebPass.Web/wwwroot/js/secret-reveal.js`
- Modify: `src/WebPass.Web/Pages/Servers/Index.cshtml`
- Test: `tests/WebPass.IntegrationTests/Secrets/RevealTests.cs`

**Interfaces:**
- Produces `Task<ReauthenticationGrant> VerifyAsync(Guid userId, string password, CancellationToken ct)`.
- Produces `Task<RevealResult> RevealAsync(Guid userId, Guid assetId, CancellationToken ct)`.

- [ ] **Step 1: Write failing reveal tests**

```csharp
[Fact]
public async Task Reveal_without_unexpired_grant_returns_forbidden()
{
    var response = await client.GetAsync($"/secrets/reveal?assetId={assetId}");
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
}
```

- [ ] **Step 2: Run it to verify failure**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~RevealTests`

Expected: FAIL because reveal handlers do not exist.

- [ ] **Step 3: Implement the server-side grant and reveal endpoint**

```csharp
public sealed record ReauthenticationGrant(Guid UserId, DateTimeOffset ExpiresAt);
```

Verify the current password using the registered Argon2id password hasher and store a protected server-side grant with five-minute absolute expiration. Reveal checks `PermissionCode.SecretReveal`, grant, asset and secret; decrypts only then; emits a redacted audit event; and sends `Cache-Control: no-store, no-cache`. The JavaScript removes the value after 30 seconds and on `pagehide`.

- [ ] **Step 4: Run reveal tests**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~RevealTests`

Expected: PASS for denied permission, absent grant, expired grant, no-store header, successful reveal and audit redaction.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Application/Secrets src/WebPass.Web/Pages/Secrets src/WebPass.Web/wwwroot/js tests
git commit -m "feat: add reauthenticated password reveal"
```

### Task 3: Add encrypted create/edit and memory-only Excel/CSV import

**Files:**
- Modify: `src/WebPass.Web/Application/Assets/ServerAssetInput.cs`
- Modify: `src/WebPass.Web/Application/Assets/ServerAssetService.cs`
- Create: `src/WebPass.Web/Application/Importing/IImportService.cs`
- Create: `src/WebPass.Web/Application/Importing/ImportPreview.cs`
- Create: `src/WebPass.Web/Infrastructure/Importing/CsvAssetParser.cs`
- Create: `src/WebPass.Web/Infrastructure/Importing/XlsxAssetParser.cs`
- Create: `src/WebPass.Web/Infrastructure/Importing/InMemoryImportStageStore.cs`
- Create: `src/WebPass.Web/Pages/Imports/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Imports/Index.cshtml.cs`
- Test: `tests/WebPass.IntegrationTests/Importing/ImportTests.cs`

**Interfaces:**
- Extends `ServerAssetInput` with `string? Password`.
- Produces `Task<ImportPreview> PreviewAsync(Stream source, ImportFileType type, Guid actorId, CancellationToken ct)`.
- Produces `Task<ImportCommitResult> CommitAsync(Guid previewId, Guid actorId, CancellationToken ct)`.

- [ ] **Step 1: Write failing import tests**

```csharp
[Fact]
public async Task Blocking_row_error_prevents_atomic_commit()
{
    var preview = await service.PreviewAsync(Csv("10.0.0.1\n10.0.0.1"), ImportFileType.Csv, actorId, default);
    Assert.True(preview.HasBlockingErrors);
    await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(preview.Id, actorId, default));
}
```

- [ ] **Step 2: Run it to verify failure**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~ImportTests`

Expected: FAIL because import service does not exist.

- [ ] **Step 3: Implement staged, encrypted import**

```csharp
public sealed record ImportPreview(Guid Id, int TotalRows, int CreateCount, int UpdateCount,
    int SkipCount, IReadOnlyList<ImportRowError> Errors, bool HasBlockingErrors);
```

Require `PermissionCode.ImportData`; reject over-10-MB, over-5,000-row, macro, invalid type, duplicate IP, out-of-subnet and malformed values. Configure request buffering to memory; immediately transform each password to `SecretEnvelope`; retain only encrypted staging records in process memory for 15 minutes. Commit all rows in one transaction, create an `ImportJob`, and audit a secret-free summary.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~ImportTests`

Expected: PASS for encrypted password storage, expiry, full rollback and errors that omit passwords.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Application/Assets src/WebPass.Web/Application/Importing src/WebPass.Web/Infrastructure/Importing src/WebPass.Web/Pages/Imports tests
git commit -m "feat: add encrypted asset import"
```

### Task 4: Add secret-free ordinary exports and administrator backup/recovery

**Files:**
- Create: `src/WebPass.Web/Application/Exporting/AssetExportService.cs`
- Create: `src/WebPass.Web/Pages/Exports/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Exports/Index.cshtml.cs`
- Create: `src/WebPass.Web/Application/Backups/BackupService.cs`
- Create: `src/WebPass.Web/Pages/Admin/Backups.cshtml`
- Create: `src/WebPass.Web/Pages/Admin/Backups.cshtml.cs`
- Test: `tests/WebPass.UnitTests/Exporting/SpreadsheetCellSanitizerTests.cs`
- Test: `tests/WebPass.IntegrationTests/Backups/BackupTests.cs`

**Interfaces:**
- Produces `Task<ExportFile> ExportAsync(ExportFormat format, ServerListQuery query, Guid actorId, CancellationToken ct)`.
- Produces `Task<BackupFile> CreateAsync(string passphrase, Guid administratorId, CancellationToken ct)`, `PreviewAsync`, and `RestoreAsync`.

- [ ] **Step 1: Write failing export and backup tests**

```csharp
[Theory]
[InlineData("=2+2", "'=2+2")]
[InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
public void Escapes_spreadsheet_formulas(string source, string expected) =>
    Assert.Equal(expected, SpreadsheetCellSanitizer.Sanitize(source));

[Fact]
public async Task Wrong_backup_passphrase_cannot_open_package()
{
    var file = await backups.CreateAsync("correct horse battery staple", adminId, default);
    await Assert.ThrowsAsync<CryptographicException>(() =>
        backups.PreviewAsync(file.OpenRead(), "wrong", adminId, default));
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~SpreadsheetCellSanitizerTests`

Expected: FAIL because export code does not exist.

- [ ] **Step 3: Implement export and backup boundaries**

```csharp
public sealed record ExportRow(string Location, string AliveStatus, string ComputerName,
    string SystemName, string BusinessIp, string? OperatingSystemVersion,
    string? DatabaseVersion, string? Notes);
```

Export only `ExportRow`, never load `ServerSecret`, wrapped keys, hashes or sessions. Require `ExportData`; audit format, filters and count. For backup, require administrator and reauthentication; derive a package key from random salt plus Argon2id passphrase; encrypt serialized ciphertext records using AES-GCM. Preview is read-only; restore is a transaction and audits one recovery action.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~BackupTests`

Expected: PASS for no secret columns, formula escaping, wrong passphrase rejection, administrator denial, preview and atomic restore.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Application/Exporting src/WebPass.Web/Application/Backups src/WebPass.Web/Pages/Exports src/WebPass.Web/Pages/Admin tests
git commit -m "feat: add safe exports and encrypted backups"
```

### Task 5: Harden production hosting and verify acceptance

**Files:**
- Create: `src/WebPass.Web/Infrastructure/Security/SecurityHeadersMiddleware.cs`
- Create: `src/WebPass.Web/Infrastructure/Security/RateLimitPolicies.cs`
- Create: `src/WebPass.Web/Pages/Health.cshtml.cs`
- Modify: `src/WebPass.Web/Program.cs`
- Create: `docs/deployment/windows-server-iis.md`
- Create: `docs/deployment/certificates-and-key-recovery.md`
- Create: `docs/deployment/backup-restore-runbook.md`
- Create: `docs/deployment/acceptance-test-record.md`
- Create: `scripts/Initialize-WebPass.ps1`
- Test: `tests/WebPass.IntegrationTests/Security/ProductionSecurityTests.cs`

**Interfaces:**
- Produces `GET /health` with only application/database availability.
- Produces HTTPS redirection, antiforgery validation, restrictive CSP and named rate-limit policies.

- [ ] **Step 1: Write failing hardening tests**

```csharp
[Fact]
public async Task Servers_page_sets_restrictive_content_security_policy()
{
    var response = await client.GetAsync("/servers");
    Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~ProductionSecurityTests`

Expected: FAIL because hardening middleware does not exist.

- [ ] **Step 3: Implement controls and runbooks**

```csharp
context.Response.Headers.ContentSecurityPolicy =
    "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'";
```

Use separate rate limits for login, reauthentication, Ping and reveal. The IIS runbook must require IIS installation before .NET Hosting Bundle, a dedicated low-privilege app-pool account, certificate private-key permission, HTTPS-only binding, LAN-only firewall scope, local-only SQL Server, offline encrypted PFX backup, and successful client certificate-trust verification.

- [ ] **Step 4: Run final verification**

Run: `dotnet test WebPass.sln -c Release`

Expected: PASS.

Run: `dotnet publish src/WebPass.Web -c Release -r win-x64 --self-contained false`

Expected: PASS and output includes `web.config`.

- [ ] **Step 5: Commit**

```bash
git add src/WebPass.Web/Infrastructure/Security src/WebPass.Web/Pages/Health* src/WebPass.Web/Program.cs docs/deployment scripts tests
git commit -m "docs: add secure IIS deployment runbooks"
```

## Self-Review

- Coverage: Tasks 1-5 implement encryption, key rotation, reauthentication, short-lived reveal, online encrypted updates, memory-only import staging, safe export, administrator backup/recovery, IIS hardening and acceptance evidence.
- Consistency: `SecretEnvelope`, `ReauthenticationGrant`, `ImportPreview`, `ExportRow` and the backup methods are defined before callers.
- Security: each source of server-password plaintext has an explicit time, memory, authorization and logging boundary.
