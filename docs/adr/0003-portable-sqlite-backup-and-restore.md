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
- **Backup from one read snapshot.** Open the source read-only, start a
  deferred read transaction, fingerprint every table and the schema (row
  count and an order-independent content hash), and copy that same snapshot
  with SQLite's online backup API into a temporary file. Verify the copy
  (`PRAGMA integrity_check`, `PRAGMA foreign_key_check`, identical
  fingerprints) and only then move it to its final name without overwrite.
  On failure, delete only that temporary file.
- **Restore only into a new file.** Refuse if the destination or a leftover
  `-wal`/`-shm` file exists; copy into a temporary file; switch it to WAL
  mode; verify it again (integrity, foreign keys, fingerprints), so the file
  that is verified is the one that is moved; then `File.Move` without
  overwrite.
- Expose both as a small .NET console tool that reuses the API's own code,
  with thin PowerShell wrappers. `Start-Local.ps1` starts the API on an
  explicit database file and refuses a missing one unless `-InitializeEmpty`
  is given.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Copying the `.db` file | Unsafe while the API runs: committed data may still be only in the `-wal` file. |
| `VACUUM INTO`, then compare with the source's row counts (the first implementation) | The copy is consistent, but SQLite cannot run `VACUUM INTO` inside a transaction, so the counts come from a later moment. A booking committed in between causes a false verification failure. |
| `VACUUM INTO` and verify the copy only on its own (integrity, foreign keys) | No false failures, but nothing shows the copy holds the data the source had. |
| `VACUUM INTO` and retry while `PRAGMA data_version` changes | Works, but under steady writes it may never succeed, and the retry logic is harder to reason about. |
| JSON/CSV export and import | Engine-independent, but must be kept in sync with every schema change, and loses triggers and constraints unless re-created. |
| Restore in place (overwrite) | One mistake destroys the only good copy. |
| Move to PostgreSQL in Docker now | Better concurrency, but adds a server to run and back up, and is not needed for a single-site deployment. |
| Managed cloud backups only | Ties recovery to the provider the owner wants to leave. |

## Consequences

- Positive: backups are ordinary files that can be copied anywhere; restore
  cannot destroy existing data; the restored database passes the same
  start-up checks as any other (covered by `DatabasePortabilityTests`), and an
  API started on it keeps tenant data and access rules
  (`RestoredDatabaseApiTests`).
- Positive: a backup taken while the API writes is verified against exactly
  the data it contains, so concurrent bookings never cause a false failure
  (`DatabaseBackupConcurrencyTests`), and a failed backup leaves no file.
- Negative: backups are not compacted, and fingerprinting reads every row
  once more. Both are negligible at this data size.
- Negative: on a database in rollback-journal mode, the read transaction
  makes writers wait until the backup ends. The API's databases use WAL.
- Negative: an `internal` test hook in `CreateVerifiedAsync` (visible to the
  test project only) exists to commit at the exact moment between snapshot
  and copy.
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
