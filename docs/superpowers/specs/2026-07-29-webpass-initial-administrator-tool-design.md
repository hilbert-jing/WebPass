# WebPass Initial Administrator Tool Design

**Date:** 2026-07-29

## Context

WebPass has a local `AppUser` entity, SQL Server persistence, Argon2id password
hashing, and administrator authorization based on `AppUser.IsAdministrator`.
The deployed application has no supported tool for creating an administrator
account outside the web interface.

The required addition is a local command-line utility that creates an
administrator directly in the configured WebPass database. It is a deployment
tool, not a web endpoint or an administrator-management system.

## Scope

The tool:

- creates one enabled administrator for each successful invocation;
- may run whether `Users` is empty or contains existing users or administrators;
- accepts a connection string and username as command-line arguments;
- reads and confirms the password through hidden interactive console input;
- reuses WebPass's existing `WebPassDbContext`, `AppUser`, and Argon2id password
  hasher;
- returns a clear process exit code and a secret-free result message.

## Out of Scope

- Checking whether the database already contains users or administrators.
- Restricting the number of administrators.
- Creating or editing ordinary users.
- Password reset or password-change workflows.
- Assigning ordinary `UserPermission` records.
- A web page, HTTP endpoint, startup hook, or background service.
- Database schema changes or EF Core migrations.
- A new password hashing algorithm or password policy.
- Changes to login, authorization, cookies, sessions, or existing auditing.

## Architecture

Add a separate `.NET 10` console project:

```text
src/WebPass.AdminInit/
  WebPass.AdminInit.csproj
  Program.cs
  AdministratorInitializer.cs
```

The project references `WebPass.Web` so it can use the existing domain entity,
EF Core context, and `Argon2PasswordHasher` without copying authentication
logic. It is added to `WebPass.sln` and published independently from the web
application.

`Program.cs` owns argument parsing, hidden password input, process exit codes,
and user-facing messages. `AdministratorInitializer` owns input normalization,
duplicate-username detection, hashing, entity construction, and the single
database write.

No initialization code is registered in `WebPass.Web` and the web application
does not invoke the utility.

## Command Contract

Example:

```powershell
WebPass.AdminInit.exe `
  --connection-string "Server=localhost\SQLEXPRESS;Database=WebPass;Integrated Security=True;TrustServerCertificate=True" `
  --username admin
```

The tool then prompts:

```text
Password:
Confirm password:
```

Password characters are not echoed. Redirected standard input is rejected so a
plaintext password is not accidentally supplied through a command pipeline or
captured in command history.

Required arguments:

- `--connection-string`: the SQL Server connection string.
- `--username`: the administrator username.

Unknown arguments, missing values, or repeated arguments are invalid usage.

## Creation Flow

1. Parse the required arguments.
2. Trim the username and require a non-empty value of at most 128 characters.
3. Read the password twice without echoing it.
4. Require a non-empty password and exact confirmation match.
5. Open `WebPassDbContext` with the supplied SQL Server connection string.
6. Check only whether the normalized username already exists.
7. Hash the password with the existing `Argon2PasswordHasher`.
8. Insert one `AppUser` with:
   - `Username` set to the normalized username;
   - `PasswordHash` set to the Argon2id result;
   - `IsAdministrator = true`;
   - `IsEnabled = true`;
   - `MustChangePassword = false`;
   - `FailedLoginCount = 0`;
   - `LockedUntil = null`.
9. Save once and report the created username.

No `UserPermission` rows are created. Existing administrator authorization
continues to use `IsAdministrator`.

## Failure Handling

The tool makes no database change for invalid arguments, invalid username,
empty password, mismatched confirmation, or duplicate username.

Exit codes:

- `0`: administrator created.
- `2`: invalid command usage or interactive input.
- `3`: username already exists.
- `1`: database or unexpected operational failure.

Error output identifies the category without including the password, password
hash, connection string, or database exception details that may contain
sensitive configuration.

If concurrent executions use the same username, the database unique index
remains the final authority. The losing invocation reports a duplicate username
and does not create a second record.

## Security Boundaries

- The password exists only in the local console process and the hashing call.
- Password and hash values are never printed or placed in arguments, files,
  logs, or audit payloads.
- The utility must be executed locally by an operator who already has database
  write access.
- The executable grants no additional SQL Server or Windows permissions.
- Repeated execution is intentionally permitted and may create multiple
  administrators with distinct usernames.
- The utility does not add an audit event because this scoped deployment command
  runs outside an authenticated WebPass session and no auditing behavior was
  requested.

## Database Impact

No entity, table, column, index, or migration is added. The existing unique
index on `Users.Username` is reused.

## Testing

Automated tests cover:

- creating an enabled administrator whose password verifies with the existing
  hasher;
- creation when ordinary users already exist;
- creation when another administrator already exists;
- duplicate username rejection without a second record;
- empty or overlength username rejection;
- empty or mismatched password input rejection before database access;
- argument parsing and stable exit-code mapping;
- result and error messages never containing password or hash text.

The full Release suite remains the regression gate for authentication,
authorization, audit, secure data operations, and production hosting.

## Deployment Documentation

Update the IIS deployment runbook with:

- the independent publish command for `WebPass.AdminInit`;
- a local execution example using integrated SQL Server authentication;
- confirmation that the utility may be deleted from the server after account
  creation if operators do not need it again.
