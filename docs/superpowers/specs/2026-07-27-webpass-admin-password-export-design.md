# WebPass Safe Export and Administrator Password Export Design

**Date:** 2026-07-27

## Context

WebPass already stores server passwords as certificate-backed AES-GCM envelopes,
supports five-minute current-password reauthentication grants bound to the current
session and user row version, and audits password reveal without recording secret
content.

The original Secure Data & Operations plan included encrypted backup and recovery.
That scope is removed. WebPass instead needs ordinary secret-free exports and a
separate administrator-only XLSX export that includes decrypted server passwords.

The application is a small internal intranet tool. The design favors a clear
authorization boundary and maintainable implementation over additional backup,
distribution, or enterprise compliance features.

## Goals

- Allow users with `ExportData` to export filtered inventory without passwords.
- Support CSV and XLSX for ordinary secret-free exports.
- Provide a separate administrator-only XLSX export containing server passwords.
- Require a valid five-minute current-password reauthentication grant before the
  administrator password export.
- Generate exports in memory without temporary files.
- Prevent spreadsheet formula interpretation for all text values.
- Audit export actions without recording passwords or cryptographic material.
- Reuse the existing inventory filters, administrator check, reauthentication
  grant, secret cipher, and audit writer.

## Out of Scope

- Database backup or restore.
- Encrypted backup packages and backup passphrases.
- Importing an exported file as a recovery operation.
- CSV or other formats for the administrator password export.
- Password-protecting or encrypting the XLSX file itself.
- Scheduled exports, background jobs, email delivery, or external storage.
- Stress testing or changes for high concurrency.

## User Experience

### Ordinary export

Users with `ExportData` see an **Export** navigation entry. The page exposes the
existing inventory filters and offers CSV or XLSX. The output contains inventory
fields only and never contains a password, ciphertext, nonce, authentication tag,
wrapped key, password hash, session data, or audit data.

### Administrator password export

Administrators see a separate **Password export** entry under administration. The
page:

1. Displays a warning that the downloaded workbook contains plaintext server
   passwords and must be handled accordingly.
2. Accepts the same inventory filters as the ordinary export.
3. Offers XLSX only.
4. Redirects to the existing current-password reauthentication flow when no valid
   grant exists.
5. Enables the export after a valid grant is present for the current session.

The five-minute grant remains reusable within its existing lifetime. This feature
does not introduce a second grant type or a new password verification mechanism.

## Authorization Boundary

Ordinary export requires `PermissionCode.ExportData`.

Administrator password export requires all of the following:

- An authenticated and enabled user.
- `IsAdministratorAsync(userId)` returns `true`.
- A valid `IReauthenticationGrantStore` entry for the same user, current
  authentication-session fingerprint, and current user row version.

The administrator password export does not rely on `ExportData` or
`SecretReveal`. Administrator status and reauthentication are its explicit
boundary. This keeps the sensitive operation independent from ordinary delegated
export permissions.

Authorization is checked in both the Razor Page policy/handler and the application
service. A direct service call cannot bypass administrator or grant validation.

## Components

### Secret-free export service

`AssetExportService` accepts an export format, `ServerListQuery`, actor ID, and
cancellation token. It:

- Verifies `ExportData`.
- Reuses the active server inventory filtering semantics.
- Projects only explicitly allowed inventory fields.
- Generates CSV or XLSX in memory.
- Writes a secret-free summary audit.

The query must not join or load `ServerSecrets`, `DataEncryptionKeys`, users,
sessions, hashes, or audit logs.

### Administrator password export service

`AdministratorPasswordExportService` accepts `ServerListQuery`, administrator ID,
and cancellation token. It:

- Verifies administrator status.
- Validates the existing reauthentication grant against the current session and
  user row version.
- Loads the filtered active assets and their optional `ServerSecret`.
- Decrypts each available password through `ISecretCipher`.
- Generates one XLSX workbook in memory.
- Writes a secret-free summary audit for success or failure.

An asset with no stored password produces an empty password cell. A secret
decryption failure aborts the entire download; WebPass does not return a partial
workbook.

### Spreadsheet sanitizer

All string cells from stored or user-entered data pass through one shared
sanitizer. Values beginning with `=`, `+`, `-`, or `@` are prefixed with an
apostrophe before being written to CSV or XLSX.

The password column uses the same sanitizer so a password cannot become a formula.
The resulting displayed password includes the original value rather than executing
it as spreadsheet content.

## Export Columns

Ordinary CSV and XLSX contain:

1. `BusinessIp`
2. `Location`
3. `AliveStatus`
4. `ComputerName`
5. `SystemName`
6. `OperatingSystemVersion`
7. `DatabaseVersion`
8. `Notes`

Administrator password XLSX contains the same columns plus:

9. `Password`

No internal IDs, row versions, subnet IDs, user IDs, or cryptographic fields are
exported.

## Data and Memory Flow

The service obtains the filtered rows from SQL Server, creates the workbook or CSV
stream in application memory, and returns the completed byte content to the Razor
Page. No temporary or staging file is written.

For the password export, plaintext exists only in the local operation scope while
the workbook is being built and in the returned download bytes. It is not stored in
application state, TempData, logs, exceptions, or audit payloads.

Download responses use attachment disposition and headers that prevent browser and
proxy caching, including `Cache-Control: no-store`.

## Audit Events

Ordinary export writes one `AssetExport` audit event containing:

- Result.
- Format.
- Applied filters.
- Exported row count.

Administrator password export writes one `AdministratorPasswordExport` event
containing:

- Result.
- Format fixed to `Xlsx`.
- Applied filters.
- Exported row count.

Audit payloads never contain exported rows, server passwords, ciphertext,
cryptographic key material, or exception details that may contain secret data.
Denied and failed administrator password export attempts are also audited.

## Error Handling

- Missing `ExportData` returns authorization denial for ordinary export.
- A non-administrator receives authorization denial before any secret is loaded.
- A missing or expired reauthentication grant redirects to the existing
  reauthentication page and does not generate a file.
- An invalid filter returns the existing safe validation behavior.
- A generation or decryption error returns no file and records a secret-free
  failure audit.
- Cancellation stops generation and does not write a success audit.

## Database Impact

No entity, table, column, index, or EF Core migration is required.

## Test Strategy

Unit tests cover:

- Spreadsheet formula sanitization.
- Exact ordinary and administrator column sets.
- Empty password cells for assets without secrets.

Integration tests cover:

- Ordinary CSV and XLSX contain no password or cryptographic columns.
- Ordinary export requires `ExportData`.
- Non-administrators cannot access password export.
- Administrators without a valid reauthentication grant cannot export passwords.
- A current-session grant enables password XLSX export.
- A grant for another session or stale user row version is rejected.
- Password XLSX contains decrypted passwords and no cryptographic fields.
- Password-shaped formulas are escaped.
- Audit events contain only format, filters, result, and row count.
- Decryption failure produces no partial download.
- Download responses use `no-store`.

The existing full Release test suite remains the regression gate for authentication,
authorization, audit, asset management, secret reveal, and import behavior.

## Risks and Boundaries

The administrator XLSX intentionally contains plaintext passwords after download.
WebPass can constrain creation and browser caching but cannot control how an
administrator stores or shares the downloaded file. The UI warning and audit event
make that boundary explicit.

Workbook encryption, document rights management, download lifecycle tracking, and
data-loss-prevention integration are possible future measures but are outside the
approved intranet scope and will not be implemented.
