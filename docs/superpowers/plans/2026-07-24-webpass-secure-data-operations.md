# WebPass Secure Data Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add server-password encryption and reveal, secure import/export including a separately reauthenticated administrator password XLSX, and IIS production hardening to the completed core platform.

**Architecture:** The application remains a single ASP.NET Core 10 application. A versioned AES data key is wrapped by a Windows certificate; password reveal needs a server-side reauthentication grant; imported passwords are encrypted before preview state is retained. IIS is the HTTPS boundary and SQL Server stays local to the server.

**Tech Stack:** .NET 10, ASP.NET Core, X.509 Windows Certificate Store, AES-GCM, Argon2id, SQL Server 2025 Express, ClosedXML, xUnit.

## Global Constraints

- Complete `2026-07-24-webpass-core-platform.md` first.
- Passwords must never appear in logs, audits, ordinary exports or disk-backed import staging.
- HTTPS and data-encryption certificates are separate.
- Password reveal requires `SecretReveal` permission, current-password reauthentication, a 5-minute grant and 30-second browser display.
- Import supports IPv4 `.xlsx` and `.csv` only, up to 10 MB and 5,000 rows, with atomic commit.
- Ordinary exports contain no passwords or cryptographic material.
- Ordinary export supports CSV/XLSX; the separately authorized administrator password export supports XLSX only.
- Each task starts with a failing test and ends with a focused commit.

---

## File Structure

- `src/WebPass.Web/Application/Secrets/`: encryption, reauthentication and reveal contracts.
- `src/WebPass.Web/Infrastructure/Secrets/`: Windows certificate access and AES-GCM.
- `src/WebPass.Web/Application/Importing/`, `Exporting/`: file use cases.
- `src/WebPass.Web/Pages/Secrets/`, `Imports/`, `Exports/`, `Admin/`: protected handlers.
- `docs/deployment/`: IIS, certificate and acceptance instructions.

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

### Task 4: Add secret-free ordinary exports and administrator password XLSX export

The approved design is
`docs/superpowers/specs/2026-07-27-webpass-admin-password-export-design.md`.
Database backup, restore and encrypted backup packages are removed from scope.

#### Task 4.1: Add export models, sanitizer and in-memory document writer

**Files:**
- Create: `src/WebPass.Web/Application/Exporting/ExportModels.cs`
- Create: `src/WebPass.Web/Application/Exporting/SpreadsheetCellSanitizer.cs`
- Create: `src/WebPass.Web/Infrastructure/Exporting/ExportDocumentWriter.cs`
- Test: `tests/WebPass.UnitTests/Exporting/SpreadsheetCellSanitizerTests.cs`
- Test: `tests/WebPass.UnitTests/Exporting/ExportDocumentWriterTests.cs`

**Interfaces:**
- Produces `enum ExportFormat { Csv, Xlsx }`.
- Produces `sealed record ExportFile(byte[] Content, string ContentType, string FileName)`.
- Produces `sealed record ExportRow(string BusinessIp, string Location, string AliveStatus, string ComputerName, string SystemName, string? OperatingSystemVersion, string? DatabaseVersion, string? Notes)`.
- Produces `sealed record PasswordExportRow(ExportRow Asset, string? Password)`.
- Produces `static string SpreadsheetCellSanitizer.Sanitize(string? value)`.
- Produces `ExportFile ExportDocumentWriter.WriteOrdinary(IReadOnlyList<ExportRow> rows, ExportFormat format)`.
- Produces `ExportFile ExportDocumentWriter.WritePasswords(IReadOnlyList<PasswordExportRow> rows)`.

- [ ] **Step 1: Write failing sanitizer tests**

```csharp
[Theory]
[InlineData("=2+2", "'=2+2")]
[InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
[InlineData("-1+2", "'-1+2")]
[InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
[InlineData("server-01", "server-01")]
[InlineData(null, "")]
public void Escapes_formula_prefixes(string? source, string expected) =>
    Assert.Equal(expected, SpreadsheetCellSanitizer.Sanitize(source));
```

- [ ] **Step 2: Run sanitizer tests to verify they fail**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~SpreadsheetCellSanitizerTests`

Expected: FAIL because `SpreadsheetCellSanitizer` does not exist.

- [ ] **Step 3: Implement the sanitizer**

```csharp
public static string Sanitize(string? value)
{
    if (string.IsNullOrEmpty(value)) return string.Empty;
    return value[0] is '=' or '+' or '-' or '@' ? $"'{value}" : value;
}
```

- [ ] **Step 4: Write failing document-shape tests**

```csharp
[Fact]
public void Ordinary_xlsx_has_exact_secret_free_headers()
{
    var file = writer.WriteOrdinary([Row(notes: "=2+2")], ExportFormat.Xlsx);
    using var book = new XLWorkbook(new MemoryStream(file.Content));
    var sheet = book.Worksheet(1);
    Assert.Equal(8, sheet.LastColumnUsed()!.ColumnNumber());
    Assert.Equal("Notes", sheet.Cell(1, 8).GetString());
    Assert.Equal("'=2+2", sheet.Cell(2, 8).GetString());
}

[Fact]
public void Password_xlsx_adds_only_sanitized_password_column()
{
    var file = writer.WritePasswords([new PasswordExportRow(Row(), "=secret")]);
    using var book = new XLWorkbook(new MemoryStream(file.Content));
    Assert.Equal("Password", book.Worksheet(1).Cell(1, 9).GetString());
    Assert.Equal("'=secret", book.Worksheet(1).Cell(2, 9).GetString());
}
```

- [ ] **Step 5: Run writer tests to verify they fail**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~ExportDocumentWriterTests`

Expected: FAIL because the models and writer do not exist.

- [ ] **Step 6: Implement models and writers**

Use the exact interfaces above. CSV uses UTF-8 and RFC 4180 quoting. XLSX uses
ClosedXML, exact eight ordinary headers, and an additional `Password` header only
for the administrator workbook. Pass every data cell through the sanitizer. Return
`stream.ToArray()` with `.csv`/`text/csv; charset=utf-8` or
`.xlsx`/`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`.
Do not create temporary files, formulas or hyperlinks.

- [ ] **Step 7: Run unit tests**

Run: `dotnet test tests/WebPass.UnitTests --filter FullyQualifiedName~Exporting`

Expected: PASS for formula escaping, CSV quoting and exact workbook columns.

- [ ] **Step 8: Commit**

```bash
git add src/WebPass.Web/Application/Exporting src/WebPass.Web/Infrastructure/Exporting tests/WebPass.UnitTests/Exporting
git commit -m "feat: add safe export document writers"
```

#### Task 4.2: Add secret-free export service and page

**Files:**
- Create: `src/WebPass.Web/Application/Exporting/AssetExportQuery.cs`
- Create: `src/WebPass.Web/Application/Exporting/AssetExportService.cs`
- Create: `src/WebPass.Web/Pages/Exports/Index.cshtml`
- Create: `src/WebPass.Web/Pages/Exports/Index.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Shared/_Layout.cshtml`
- Modify: `src/WebPass.Web/Program.cs`
- Test: `tests/WebPass.IntegrationTests/Exporting/AssetExportTests.cs`
- Test: `tests/WebPass.IntegrationTests/Exporting/ExportPageTests.cs`

**Interfaces:**
- Produces `IQueryable<ServerAsset> AssetExportQuery.Build(WebPassDbContext db, ServerListQuery query)`.
- Produces `Task<ExportFile> AssetExportService.ExportAsync(ExportFormat format, ServerListQuery query, Guid actorId, CancellationToken ct)`.
- Consumes `PermissionAuthorizationHandler`, `ExportDocumentWriter`, `AuditWriter`, and `PermissionCode.ExportData`.

- [ ] **Step 1: Write failing service tests**

```csharp
[Fact]
public async Task Ordinary_export_requires_permission_and_has_no_secret_columns()
{
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
        () => denied.ExportAsync(ExportFormat.Xlsx, new(), deniedUserId, default));
    var file = await allowed.ExportAsync(
        ExportFormat.Xlsx, new ServerListQuery(Search: "server-01"), exporterId, default);
    using var book = new XLWorkbook(new MemoryStream(file.Content));
    var headers = book.Worksheet(1).Row(1).CellsUsed().Select(x => x.GetString());
    Assert.DoesNotContain("Password", headers);
    Assert.DoesNotContain("Ciphertext", headers);
}
```

Also test `Search`, `SubnetId`, and `Status`; exclusion of archived/generated pool
rows; and an `AssetExport` audit containing only `format`, `search`, `subnetId`,
`status`, and `rowCount`.

- [ ] **Step 2: Run service tests to verify they fail**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~AssetExportTests`

Expected: FAIL because the service and query do not exist.

- [ ] **Step 3: Implement the shared active-asset query**

Reject `IncludeArchived` or `PoolMode` with `ArgumentException`; ignore paging
fields. Start with `ServerAssets.AsNoTracking().Where(x => !x.IsArchived)`, apply
the three approved filters with the same matching fields as
`ServerAssetService.ListAsync`, and order by `BusinessIpNumber`.

- [ ] **Step 4: Implement the ordinary export service**

Check `ExportData` before querying. Project directly to `ExportRow`; never reference
`ServerSecrets`, keys, users, hashes, sessions, or audit logs. Generate the selected
format in memory and write one secret-free `AssetExport` audit. Audit denial/failure
without rows, secrets, or exception text; do not convert cancellation to failure.

- [ ] **Step 5: Run service tests**

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~AssetExportTests`

Expected: PASS for authorization, filters, projection, formats and audit payload.

- [ ] **Step 6: Write failing page tests**

```csharp
[Fact]
public async Task Download_requires_antiforgery() =>
    Assert.Equal(HttpStatusCode.BadRequest,
        (await client.PostAsync("/exports?handler=Download", new FormUrlEncodedContent([]))).StatusCode);

[Theory]
[InlineData("Csv", "text/csv")]
[InlineData("Xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
public async Task Download_is_a_no_store_attachment(string format, string contentType)
{
    using var response = await PostWithAntiforgeryAsync("/exports?handler=Download", ("Format", format));
    Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
    Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
}
```

- [ ] **Step 7: Implement and test the ordinary export page**

Use `[Authorize(Policy = PermissionCode.ExportData)]`, a POST download handler,
bound `ServerListQuery`, and CSV/XLSX selector. Set `Cache-Control: no-store` and
`Pragma: no-cache`, then return `File(file.Content, file.ContentType,
file.FileName)`. Register `ExportDocumentWriter` and `AssetExportService` as scoped services and show the navigation entry only
when `ExportData` is allowed.

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~ExportPageTests`

Expected: PASS for authorization, antiforgery, both formats and response headers.

- [ ] **Step 8: Commit**

```bash
git add src/WebPass.Web/Application/Exporting src/WebPass.Web/Pages/Exports src/WebPass.Web/Pages/Shared/_Layout.cshtml src/WebPass.Web/Program.cs tests/WebPass.IntegrationTests/Exporting
git commit -m "feat: add secret-free inventory exports"
```

#### Task 4.3: Add reauthenticated administrator password XLSX

**Files:**
- Create: `src/WebPass.Web/Application/Exporting/AdministratorPasswordExportService.cs`
- Create: `src/WebPass.Web/Pages/Admin/PasswordExport.cshtml`
- Create: `src/WebPass.Web/Pages/Admin/PasswordExport.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Shared/_Layout.cshtml`
- Modify: `src/WebPass.Web/Program.cs`
- Test: `tests/WebPass.IntegrationTests/Exporting/AdministratorPasswordExportTests.cs`
- Test: `tests/WebPass.IntegrationTests/Exporting/AdministratorPasswordExportPageTests.cs`

**Interfaces:**
- Produces `Task<ExportFile> AdministratorPasswordExportService.ExportAsync(ServerListQuery query, Guid administratorId, CancellationToken ct)`.
- Consumes `IsAdministratorAsync`, `IReauthenticationGrantStore`, `IAuthenticationSessionFingerprint`, `ISecretCipher`, `AssetExportQuery`, `ExportDocumentWriter`, and `AuditWriter`.
- Page uses `PermissionCode.AdministratorPolicy` and XLSX only.

- [ ] **Step 1: Write failing service boundary tests**

```csharp
[Fact]
public async Task Password_export_requires_admin_and_current_session_grant()
{
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
        () => service.ExportAsync(new(), ordinaryUserId, default));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
        () => service.ExportAsync(new(), administratorId, default));
    await grants.StoreAsync(new ReauthenticationGrant(
        administratorId, fingerprint.GetCurrent(), adminRowVersion, now.AddMinutes(5)), default);
    var file = await service.ExportAsync(new(), administratorId, default);
    using var book = new XLWorkbook(new MemoryStream(file.Content));
    Assert.Equal("server-password", book.Worksheet(1).Cell(2, 9).GetString());
}
```

Add cases for another session, stale user row version, expired grant, missing
secret, formula-shaped password, decryption failure, and secret-free denied/failure
audits.

- [ ] **Step 2: Implement and test the administrator service**

Check administrator status before loading assets or secrets. Load the enabled user
row version and call `HasValidGrantAsync(administratorId,
sessionFingerprint.GetCurrent(), administrator.RowVersion, ct)`. Use
`AssetExportQuery.Build`, left-join optional `ServerSecret`, reconstruct
`SecretEnvelope`, decrypt with `ISecretCipher`, and pass method-local
`PasswordExportRow` values to `WritePasswords`. Abort on any failure and audit only
result, fixed `Xlsx` format, filters and row count.

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~AdministratorPasswordExportTests`

Expected: PASS for administrator/grant boundaries, decrypted and empty cells,
formula escaping, all-or-nothing failure and redacted audits.

- [ ] **Step 3: Write failing page tests**

```csharp
[Fact]
public async Task Non_admin_cannot_open_password_export() =>
    Assert.Equal(HttpStatusCode.Forbidden,
        (await ordinaryClient.GetAsync("/admin/password-export")).StatusCode);

[Fact]
public async Task Admin_without_grant_redirects_to_reauthentication()
{
    using var response = await PostWithAntiforgeryAsync(
        administratorClient, "/admin/password-export?handler=Download");
    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    Assert.StartsWith("/secrets/reauthenticate", response.Headers.Location!.OriginalString);
}
```

Also test antiforgery, visible plaintext warning, absence of a format selector,
XLSX content type/file name, `no-store`, and administrator-only navigation.

- [ ] **Step 4: Implement and test the isolated administrator page**

Apply `[Authorize(Policy = PermissionCode.AdministratorPolicy)]`. Display filters
and the plaintext warning, but no format selector. POST calls the service and
returns XLSX with `no-store`/`no-cache`. When the administrator lacks a valid grant,
redirect locally:

```csharp
return RedirectToPage(
    "/Secrets/Reauthenticate",
    new { ReturnUrl = Url.Page("/Admin/PasswordExport") });
```

Register the administrator service as scoped and show its navigation entry only
when `IsAdministratorAsync` is true.

Run: `dotnet test tests/WebPass.IntegrationTests --filter FullyQualifiedName~AdministratorPasswordExportPageTests`

Expected: PASS for admin policy, reauthentication redirect, XLSX-only UI,
antiforgery and cache headers.

- [ ] **Step 5: Run full verification**

```bash
dotnet test WebPass.sln -c Release --no-restore
.tools/dotnet-ef migrations has-pending-model-changes --project src/WebPass.Web --startup-project src/WebPass.Web --configuration Release --no-build
git diff --check
```

Expected: all tests pass, EF reports no pending model changes, and diff check exits
0. Do not add a migration.

- [ ] **Step 6: Commit**

```bash
git add src/WebPass.Web/Application/Exporting/AdministratorPasswordExportService.cs src/WebPass.Web/Pages/Admin/PasswordExport.cshtml src/WebPass.Web/Pages/Admin/PasswordExport.cshtml.cs src/WebPass.Web/Pages/Shared/_Layout.cshtml src/WebPass.Web/Program.cs tests/WebPass.IntegrationTests/Exporting
git commit -m "feat: add administrator password export"
```

### Task 5: Harden production hosting and verify acceptance

**Files:**
- Create: `src/WebPass.Web/Infrastructure/Security/SecurityHeadersMiddleware.cs`
- Create: `src/WebPass.Web/Infrastructure/Security/RateLimitPolicies.cs`
- Create: `src/WebPass.Web/Pages/Health.cshtml.cs`
- Modify: `src/WebPass.Web/Program.cs`
- Create: `docs/deployment/windows-server-iis.md`
- Create: `docs/deployment/certificates-and-key-recovery.md`
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

- Coverage: Tasks 1-5 implement encryption, key rotation, reauthentication, short-lived reveal, online encrypted updates, memory-only import staging, secret-free export, separately authorized administrator password XLSX export, IIS hardening and acceptance evidence.
- Consistency: `SecretEnvelope`, `ReauthenticationGrant`, `ImportPreview`, `ExportRow`, `PasswordExportRow` and both export services are defined before callers.
- Security: each source of server-password plaintext has an explicit time, memory, authorization and logging boundary.
