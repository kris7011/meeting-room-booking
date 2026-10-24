# Phase 4A: Run Room Booking locally and move SQLite data safely

Room Booking can run without a paid hosting service. The backend remains the
same .NET 10 API, with its data stored in a SQLite database file. Microsoft
sign-in still requires an internet connection to Microsoft and a valid Entra
app registration.

## Requirements

- Windows PowerShell and the .NET 10 SDK.
- A local database, either the existing one or a restored backup.
- The API registration's Entra Application (client) ID. This is a public
  identifier, not a client secret.
- For Android development, a USB-connected phone and
  `adb reverse tcp:5003 tcp:5003`.

**Important:** The released Android APK does not yet support choosing an
API server from within the app. `EXPO_PUBLIC_API_URL` is embedded in the
JavaScript bundle. The current Samsung development build can use the local
API through `.env.local` and a Metro restart. Runtime server selection is a
separate mobile feature and is **not** included in this backend PR.

## Start the existing database locally

Open PowerShell in the backend repository:

```powershell
cd C:\Users\krisr\source\repos\meeting-room-booking-entra
$env:Entra__ApiClientId = "<API application client ID>"
.\scripts\Start-Local.ps1
```

The default database is
`src/MeetingRoomBooking.Api/meeting-room-booking.db`.
If the file is missing, the script stops instead of silently creating an
empty database.

To intentionally create a new database, choose a **new filename** and
explicitly provide `-InitializeEmpty`:

```powershell
.\scripts\Start-Local.ps1 -DatabaseFile "C:\RoomBooking\data\new.db" -InitializeEmpty
```

To start with a previously restored database:

```powershell
.\scripts\Start-Local.ps1 -DatabaseFile "C:\RoomBooking\data\restored.db"
```

The script binds the API only to `http://localhost:5003`, requires
Microsoft sign-in for organization data
(`LegacyApi__AllowAnonymous=false`), and prints the selected absolute
database path. It does not expose the API to Wi-Fi or the public internet.

Verify `http://localhost:5003/health` returns `Healthy`.
When necessary, existing SQLite databases are migrated on API startup,
after a verified migration backup.

## Create a manually verified backup

The snapshot is consistent even while the API runs, but bookings made
after it are not included. For a final backup (for example before
cancelling hosting), stop the API first:

```powershell
.\scripts\Backup-Local.ps1
```

By default, this backs up the same database used by `Start-Local.ps1`
to `$HOME\RoomBookingBackups`, outside the repository. Specify the paths
if the API uses another database:

```powershell
.\scripts\Backup-Local.ps1 -DatabaseFile "C:\RoomBooking\data\restored.db" -BackupDirectory "D:\Backups\RoomBooking"
```

The tool opens the database read-only and starts one read transaction.
Inside it, the tool records a fingerprint of every table and of the
schema (row count and a hash of the rows' contents), then copies that
same snapshot with SQLite's online backup API. Bookings committed by the
running API meanwhile are not in this backup, and they cannot make its
verification fail. The copy must then pass `PRAGMA integrity_check` and
`PRAGMA foreign_key_check` and have the same fingerprints. It is written
under a temporary `.partial-<id>.tmp` name and gets its final name only
once verified, so a failed backup leaves no file behind and an existing
file is never replaced. Failures are reported instead of claiming that
the backup succeeded. The filename
format is `<database-name>.manual-<UTC-timestamp>.db`; the API's own
automatic backups before a migration are named
`<database-name>.pre-<migration>-<UTC-timestamp>.db`.

Also keep a verified copy on another physical drive or at a secure
location **outside the hosting provider's account**. Backups retained
only on the hosting account do not provide a safe exit strategy.
Database copies include identities, invitations, and booking data;
handle them as private information.

## Restore into a NEW database file (no overwrites)

This procedure requires a SQLite database backup. A future PostgreSQL
deployment will need a separate, documented export/import workflow.

```powershell
.\scripts\Restore-Local.ps1 `
  -BackupFile "D:\Backups\RoomBooking\meeting-room-booking.manual-20261023T120000000Z.db" `
  -DestinationFile "C:\RoomBooking\data\from-cloud.db"
```

The tool:

1. Checks `PRAGMA integrity_check` and `PRAGMA foreign_key_check`.
2. Creates a SQLite copy in a temporary file.
3. Switches the copy to WAL journal mode, like the databases the API
   creates (backups use a rollback journal, so each is a single file).
4. Verifies integrity, foreign keys and the fingerprint of every table
   and of the schema again.
5. Moves the copy into place only if neither the destination nor a
   leftover `-wal`/`-shm` file of that name exists.

An existing database is never overwritten. Always choose a new
destination filename. Then run
`Start-Local.ps1 -DatabaseFile "C:\RoomBooking\data\from-cloud.db"`.

Before the **final** cloud backup, stop all writes to the hosted API.
Verify the restored organization names, roles, meeting rooms, and
bookings locally. Do not cancel hosting until recovery has been tested.

## Local Android development

In `room-booking-mobile`, set
`EXPO_PUBLIC_API_URL=http://localhost:5003` in `.env.local`.
For a Samsung device connected by USB:

```powershell
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb reverse tcp:5003 tcp:5003
& $adb reverse tcp:8081 tcp:8081
npx expo start --dev-client --clear --port 8081
```

Sending bearer tokens over local HTTP is allowed only in the
development build via localhost/ADB. A release build or remote access
requires HTTPS and a separate configuration. Do not send bearer tokens
to an unencrypted HTTP address on Wi-Fi.

## Limitations

- Hosted SQLite requires persistent disk storage and an appropriate
  deployment topology (typically one writing API instance), or data
  could disappear during redeployment.
- This provides manual export and recovery, **not** automatic
  synchronization between the cloud and the local PC. Do not use both
  database copies as though they shared updates.
- Microsoft Entra is an external identity provider. Fully offline
  sign-in is a separate project.
- Run one `Start-Local.ps1` per checkout at a time. It builds the API in
  Debug in place, and a running instance locks
  `bin\Debug\net10.0\MeetingRoomBooking.Api.exe`, so a second start fails
  at the build step (MSB3027) before any database is opened. Stop the
  first API, or start the second from another checkout.
- A backup taken while the API is writing contains the bookings
  committed before its snapshot, not those after it. The API's databases
  use WAL mode, so the backup does not block bookings. A database in
  rollback-journal mode would make writers wait until the backup's read
  transaction ends.
- If the backup process is killed, a `*.partial-<id>.tmp` file can stay
  in the backup folder. It is never a verified backup; delete it.
- The fingerprint is a sum of truncated SHA-256 row hashes. It detects an
  accidentally different or incomplete copy, not deliberate tampering.
- Backing up a WAL-mode database opens it read-only, and SQLite then
  leaves empty `-wal`/`-shm` files next to the source. They are harmless;
  the database file itself is not changed.
- `Backup-Local.ps1`, `Restore-Local.ps1` and `Start-Local.ps1` use
  Windows paths and are tested on Windows PowerShell 5.1 only. CI covers
  the backup and restore code itself (`DatabasePortabilityTests`,
  `RestoredDatabaseApiTests`) on isolated test databases.

## Recovery test on a copy of the real data (2026-10-24)

Run on Windows PowerShell 5.1 against a file **copy** of the development
database; the original's SHA-256 was the same before and after.

- `Backup-Local.ps1` produced a verified `manual` backup, and
  `Restore-Local.ps1` restored it into a new file. Restoring again onto
  that file, or onto the development database path, was refused and left
  both files unchanged.
- The restored file matched the copy row for row in every table
  (organizations, meeting rooms, bookings, memberships, users,
  invitations, audit events, migration history), with the same schema,
  all 7 migrations, the 4 integrity triggers, `integrity_check` = ok, no
  foreign-key violations, and WAL journal mode.
- The API started on the restored file without migrating or taking a
  start-up backup. Another `Start-Local.ps1` API was already running, so
  this one was built into a separate folder and started with exactly the
  settings `Start-Local.ps1` sets (see the first limitation above). Every protected endpoint answered 401 without a token
  and with a malformed, unsigned (`alg: none`), HS256 or foreign-key RS256
  token.
- **Not covered:** a signed-in request with a real Microsoft token, which
  needs an interactive Microsoft sign-in on the phone. The signed-in
  paths are covered by the integration tests with a test signing key.

### Follow-up checks on the same copies (2026-10-24)

Two gaps in the test above were closed by hand, again only on copies
(the restored file from the test above, copied once more). These checks
used a script outside the repository and are not automated tests.

- **Invariants still enforce, not just exist.** On a throwaway copy of
  the restored file, inside a transaction that was rolled back, the
  database refused: an overlapping booking (same slot and partial
  overlap, `booking_overlap`), an update that moves a booking onto
  another, a booking that ends before it starts (`CK_Bookings_TimeRange`),
  a booking in one organisation for another organisation's room
  (composite foreign key), and deleting or demoting an organisation's
  only Owner (`last_owner`). A booking that starts when another ends was
  accepted. The copy was unchanged afterwards. Both memberships in this
  data are sole Owners, so the role check constraint could not be
  isolated here; the last-owner trigger rejects that write first.
- **Commits that are only in the WAL are captured on real data.** In the
  test above, the copy's WAL was empty. Here, a booking was committed to
  a copy of the restored file and the connection kept open without a
  checkpoint, like a running API: the row existed only in the `-wal`
  file (the main file was byte-for-byte unchanged, and a copy of the main
  file alone did not contain it). `RoomBooking.DatabaseTool backup` and
  `restore` then produced a backup and a new database that both
  contained the row; the restored file was a single file in WAL mode,
  `integrity_check` = ok, and identical in schema and content to the
  source once that was closed.
- The original development database and the safety copy taken before
  the review still had the SHA-256 recorded before any Phase 4A test.
- `dotnet test` on Windows: 295/295 passed on `dbe2a31`.

### Backup during concurrent writes (2026-10-24)

The earlier backup ran `VACUUM INTO` and then counted the source's rows in
separate statements. `DatabaseBackupConcurrencyTests` reproduced the false
failure on that code ("does not have the same rows" while a second
connection committed bookings). The backup now fingerprints and copies
one read-transaction snapshot (see above).

- Automated (`DatabaseBackupConcurrencyTests`, `DatabaseBackupTests`):
  repeated backups and restores while bookings are committed, a booking
  committed exactly between snapshot and copy, a booking saved but not
  yet committed, clean-up after a failed backup, and no overwriting of an
  existing file. `dotnet test` on Windows: 303/303 passed.
- By hand, on a file copy of the development database: `backup` and
  `restore` with `RoomBooking.DatabaseTool` succeeded; the backup was a
  single file, and the original's SHA-256 was unchanged.
