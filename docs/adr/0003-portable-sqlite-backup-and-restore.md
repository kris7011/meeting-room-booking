# ADR 0003: Portable operation with SQLite snapshots and restore to a new file

- **Status:** Proposed (pull request #13, Phase 4A)
- **Code:** `Data/DatabasePortability.cs`, `Data/DatabaseBackup.cs`,
  `tools/RoomBooking.DatabaseTool/`, `scripts/Start-Local.ps1`,
  `scripts/Backup-Local.ps1`, `scripts/Restore-Local.ps1`

## Context

The owner may stop paying for hosting. Room Booking must then keep working on
one Windows PC, with all organisations, members, rooms and bookings intact.
Constraints:

- No mandatory paid service other than Microsoft Entra sign-in.
- Real data must never be overwritten or lost during backup or restore.
- The procedure must be simple enough to run by hand under pressure.

## Decision

- Keep **SQLite** as the database; the whole state is one file.
- **Backup** with `VACUUM INTO`, which writes a transactionally consistent
  copy even while the API runs, then verify it: `PRAGMA integrity_check` and
  an identical row count per table.
- **Restore only into a new file.** Refuse if the destination or a leftover
  `-wal`/`-shm` file exists; copy into a temporary file; verify again; switch
  to WAL mode; then `File.Move` without overwrite.
- Expose both as a small .NET console tool that reuses the API's own code,
  with thin PowerShell wrappers. `Start-Local.ps1` starts the API on an
  explicit database file and refuses a missing one unless `-InitializeEmpty`
  is given.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Copying the `.db` file | Unsafe while the API runs: committed data may still be only in the `-wal` file. |
| JSON/CSV export and import | Engine-independent, but must be kept in sync with every schema change, and loses triggers and constraints unless re-created. |
| Restore in place (overwrite) | One mistake destroys the only good copy. |
| Move to PostgreSQL in Docker now | Better concurrency, but adds a server to run and back up, and is not needed for a single-site deployment. |
| Managed cloud backups only | Ties recovery to the provider the owner wants to leave. |

## Consequences

- Positive: backups are ordinary files that can be copied anywhere; restore
  cannot destroy existing data; the restored database passes the same
  start-up checks as any other (covered by `DatabasePortabilityTests`).
- Negative: SQLite-specific. A hosted deployment on another engine would need
  its own export/import path.
- Negative: no automatic schedule or off-site copy; backups are manual.
- Negative: the tool does not check that a backup's schema version is not
  newer than the running code.
- Negative: getting the file *out* of a hosting provider depends on that
  provider allowing file access.

## When to revisit

If the system needs several API instances, high write concurrency or
automated off-site backups, move to a server database (for example
PostgreSQL) with its native backup tooling, and replace this ADR.
