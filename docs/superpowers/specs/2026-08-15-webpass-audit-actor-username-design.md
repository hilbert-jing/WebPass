# WebPass Audit Actor Username Design

## Goal

Persist both the stable actor user ID and the username visible when an audit event is written, show that username on `/audit`, and backfill resolvable production history during the database migration.

## Scope

This change is limited to audit persistence, audit presentation, the corresponding EF Core migration, and focused tests. Login, passwords, permissions, secrets, ping, imports, exports, subnets, and server asset behavior remain unchanged.

## Current Behavior and Root Cause

`AuditLog` stores only nullable `ActorUserId`. `AuditWriter` copies only that ID, and `/audit` renders `ActorUserId.ToString()`. Consequently, human operators see a GUID rather than a username, and the database has no immutable username snapshot.

## Chosen Design

Add nullable `AuditLog.ActorUsername`, configured as `nvarchar(128)`, matching `Users.Username`. `ActorUserId` remains unchanged. `AuditWriter` resolves the username centrally from `Users` whenever an actor ID is present and writes the result in the same audit save. A missing user produces a null snapshot and does not block the audit write.

The audit page uses this display order:

1. Non-blank `ActorUsername`.
2. `系统` when `ActorUserId` is null.
3. `未知用户（{ActorUserId}）` when an ID exists but no snapshot can be resolved.

No foreign key is added between audit logs and users. Audit records must remain readable when a user row is absent, and the stored ID must remain stable independently of the user lifecycle.

## Migration and Backfill

The migration adds nullable `ActorUsername` and then performs a SQL Server joined update from `AuditLogs.ActorUserId` to `Users.Id`. Rows with a null actor ID or no matching user remain null. The migration therefore tolerates system events and orphaned historical IDs. `Down` drops only the new column.

Historical values are necessarily the username present when the migration runs; only new audit entries can record a true event-time snapshot.

## Alternatives Rejected

- Passing usernames through every `AuditEntry` call site avoids one lookup but creates broad, error-prone changes and permits ID/name mismatches.
- Joining `Users` only when rendering `/audit` does not preserve a snapshot and loses names after later user changes or removal.
- A required column or a foreign key would make system and orphaned audit events unsafe.

## Testing

Keep coverage intentionally small:

- One `AuditWriter` test proves resolved snapshots are stored and a missing user does not prevent writing.
- The existing `/audit` presentation test proves username, system, and unresolved-user display behavior.
- One SQL Server migration test proves matched history is backfilled while system and orphaned rows remain readable and their IDs stay unchanged.

Run focused tests first, then the solution build/test and EF migration consistency checks that the available dependency environment permits.

## Deployment and Rollback

`DEPLOYMENT.md` already requires a clean build, a newly generated migration bundle for the same commit, a stopped site, a verified full database backup, and successful bundle execution before switching the release. No deployment documentation change is needed. Database rollback follows the documented full-backup restore boundary rather than ad hoc reverse SQL.

## Risks

- Each audit write with an actor ID adds one user lookup.
- Historical usernames may reflect migration-time rather than event-time values.
- Unknown-login records whose existing `ActorUserId` is null continue to display as system events; login behavior is out of scope.
- The current local offline package source is incomplete, so source rebuild verification may require the repository's documented offline dependency kit.
