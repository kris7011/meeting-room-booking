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

The tool uses SQLite `VACUUM INTO`, verifies database integrity, and
compares row counts for every table against the source. Failures are
reported instead of claiming that the backup succeeded. The filename
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
   creates (`VACUUM INTO` backups use a rollback journal).
4. Verifies integrity and row counts across all tables again.
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
- A Windows/PowerShell recovery test with a **copy** of the real data
  is still necessary. CI runs against isolated test databases only.
