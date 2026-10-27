# Meeting Room Booking

A small fullstack application for creating and managing meeting room reservations.

The project was developed as a technical assignment with emphasis on clear structure, understandable business rules, useful error handling and automated tests.

## Features

The application supports:

- Viewing available meeting rooms.
- Viewing existing bookings.
- Creating bookings.
- Updating bookings.
- Deleting bookings.
- Selecting a meeting room.
- Client-side form validation.
- Server-side validation.
- Prevention of overlapping bookings.
- Clear validation, not-found and conflict messages.
- Responsive desktop and mobile layouts.
- Persistent local storage through SQLite.

## Technology stack

### Backend

- .NET 10
- ASP.NET Core Minimal API
- Entity Framework Core 10
- SQLite
- Swagger/OpenAPI

### Frontend

- Angular 21
- TypeScript
- Angular Reactive Forms
- Angular Signals
- RxJS
- Tailwind CSS 4
- PostCSS

### Testing and automation

- xUnit
- ASP.NET Core `WebApplicationFactory`
- SQLite based integration tests
- Vitest
- Angular HTTP testing utilities
- GitHub Actions

## Project structure

The backend is organised by feature (`src/MeetingRoomBooking.Api/Features/Bookings`, `Organizations`, `Rooms`, `Users`, `Authentication`, `Audit`), with persistence in `Data/`. The Angular client is in `src/meeting-room-booking-web`, the integration tests in `tests/`, and the Phase 4A database tool in `tools/`. [docs/PROJECT_WALKTHROUGH.md](docs/PROJECT_WALKTHROUGH.md) explains the structure, the request flow and the design decisions in detail; [docs/adr](docs/adr/README.md) records the main architectural decisions.

## Domain model

### Meeting room

A meeting room contains:

- An identifier.
- A name.
- A capacity.

The application is seeded with the following rooms:

| Room               | Capacity |
| ------------------ | -------: |
| Focus Room         |        4 |
| Collaboration Room |        8 |
| Board Room         |       12 |

### Booking

A booking contains:

- An identifier.
- A meeting room identifier.
- A title.
- The name of the organiser.
- A start time.
- An end time.
- A creation timestamp.

The `Booking` entity validates its own core invariants, including required values, maximum text lengths and valid time ranges.

## API endpoints

```text
GET    /health

GET    /api/me                                         (Microsoft Entra access token; read-only)
POST   /api/me/registration                            (Microsoft Entra access token)
POST   /api/organizations/{organizationId}/invitations (Owner or Admin of the organisation)
POST   /api/invitations/accept                         (the invited, registered user)

GET    /api/organizations                              (a registered user's organisations and roles)
POST   /api/organizations                              (any registered user; Idempotency-Key header)

Every endpoint below: a valid access token and a membership of {organizationId}
GET    /api/organizations/{organizationId}                       (details, your role, counts)
PUT    /api/organizations/{organizationId}                       (Owner: rename)
GET    /api/organizations/{organizationId}/rooms                 (?includeArchived=true: Owner/Admin)
GET    /api/organizations/{organizationId}/rooms/available       (?startUtc&endUtc&minCapacity)
POST   /api/organizations/{organizationId}/rooms                 (Owner/Admin)
PUT    /api/organizations/{organizationId}/rooms/{id}            (Owner/Admin)
POST   /api/organizations/{organizationId}/rooms/{id}/archive    (Owner/Admin)
POST   /api/organizations/{organizationId}/rooms/{id}/restore    (Owner/Admin)
GET    /api/organizations/{organizationId}/members               (Owner/Admin)
PUT    /api/organizations/{organizationId}/members/{userId}/role (Owner)
DELETE /api/organizations/{organizationId}/members/{userId}      (Owner, or the member leaving)
GET    /api/organizations/{organizationId}/invitations           (Owner/Admin: open invitations)
DELETE /api/organizations/{organizationId}/invitations/{id}      (Owner; Admin for Member invitations)
GET    /api/organizations/{organizationId}/bookings              (?fromUtc&toUtc)
GET    /api/organizations/{organizationId}/bookings/{id}
POST   /api/organizations/{organizationId}/bookings
PUT    /api/organizations/{organizationId}/bookings/{id}         (Owner/Admin, or the Member who made it)
DELETE /api/organizations/{organizationId}/bookings/{id}         (Owner/Admin, or the Member who made it)

The original endpoints, always on the legacy organisation (see "The original endpoints")
GET    /api/rooms
GET    /api/rooms/available
GET    /api/bookings
GET    /api/bookings/{id}
POST   /api/bookings
PUT    /api/bookings/{id}
DELETE /api/bookings/{id}
```

Swagger is available in the Development environment at:

```text
http://localhost:5003/swagger
```

## Example booking request

```json
{
  "roomId": 1,
  "title": "Sprint planning",
  "bookedBy": "Kris Larsen",
  "startUtc": "2030-01-10T09:00:00Z",
  "endUtc": "2030-01-10T10:00:00Z"
}
```

## Overlap rule

A meeting room cannot have two bookings whose time periods overlap.

Two bookings overlap when:

```text
existing.StartUtc < requested.EndUtc
AND
existing.EndUtc > requested.StartUtc
```

This means adjacent bookings are allowed.

```text
Existing booking: 09:00-10:00
New booking:      10:00-11:00
```

These bookings do not overlap.

The overlap rule is checked both when a booking is created and when an existing booking is updated.

During an update, the booking being changed is excluded from its own overlap query. This prevents a booking from being treated as a conflict with itself.

The rule is enforced by the API rather than only by the frontend. This ensures that the rule still applies when requests come from clients other than the Angular application.

## Bookings must start in the future

A new booking is rejected when its `startUtc` is equal to or earlier than the server's current time. The comparison is between instants, so the offset a client sends (`+02:00`, `Z`, …) does not matter: `2026-10-25T02:45:00+02:00` and `2026-10-25T02:45:00+01:00` are an hour apart, although both read 02:45 in Copenhagen on the night summer time ends.

When an existing booking is updated, the same rule applies if its start or end changes, so a booking cannot be moved into the past (or have its period changed once it has started). Updates that keep the period, such as a corrected title, are still allowed for past bookings, and past bookings can be read as before.

The rejection is a `400 Bad Request` validation problem on `startUtc`:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "startUtc": ["The start time must be later than the current time."]
  }
}
```

The current time comes from .NET's `TimeProvider` (registered as `TimeProvider.System`). The integration tests replace it with a clock they set to exact instants (`TestTimeProvider`). Their default "now" is 1 January 2029, before all bookings the tests create, so the tests do not start failing as real time passes.

## Error handling

Expected API errors use appropriate HTTP status codes and Problem Details responses.

### `400 Bad Request`

Used for invalid input, including:

- Missing title.
- Missing organiser.
- Invalid meeting-room identifier.
- Missing start or end time.
- End time earlier than or equal to start time.
- Start time equal to or earlier than the server's current time (see [Bookings must start in the future](#bookings-must-start-in-the-future)).
- Text values that exceed the allowed maximum length.

### `404 Not Found`

Used when:

- A requested booking does not exist.
- A selected meeting room does not exist.

### `409 Conflict`

Used when the selected room already has an overlapping booking.

Example:

```json
{
  "type": "about:blank",
  "title": "Meeting room is unavailable",
  "status": 409,
  "detail": "The meeting room is already booked during the selected period.",
  "code": "booking_overlap"
}
```

The Angular frontend translates expected API errors into clear user-facing messages.

## Data storage

SQLite was selected because it provides real database behaviour while keeping the project easy to run locally.

It demonstrates:

- Entity Framework Core configuration.
- Entity relationships.
- Database migrations.
- Persistent data.
- Relational constraints.
- Real overlap queries.
- Database backed integration tests.

The local SQLite database file is excluded from Git.

The database schema is recreated from Entity Framework Core migrations, and the API automatically applies pending migrations when it starts.

### Organisations

Every meeting room belongs to an organisation, and a booking always belongs to its room's organisation; the database enforces this with a foreign key on `(OrganizationId, RoomId)`. All rooms and bookings that existed before organisations were introduced live, unchanged and with their original IDs, in the legacy organisation (ID 1). Nobody becomes a member of it automatically.

`OrganizationId` has no database default: a row that does not name its organisation is rejected. The original endpoints (`/api/rooms`, `/api/rooms/available`, `/api/bookings`) only see the legacy organisation and require a signed-in member of it, unless the Development-only anonymous demo mode is on; see [The original endpoints](#the-original-endpoints).

### Identities, memberships, invitations and audit events

The data model for self-service organisations is in place. Registration creates users and identities, configuration names the first Owner of the legacy organisation, and invitations add everyone else; see [Registration and organisation access](#registration-and-organisation-access).

| Table | Key and relations | Enforced by the database |
| --- | --- | --- |
| `UserIdentities` | One row per way a user signs in; `UserId` → `Users`. Unique on `(Issuer, Subject)`: the validated `iss` claim and the issuer's immutable user ID (`oid`). | Provider is `Entra` or `ExternalId`; issuer and subject are not empty; a verified e-mail is lower case. The e-mail is deliberately not unique, so two identities with the same address are never treated as one person. |
| `Memberships` | Key `(OrganizationId, UserId)`, so one membership per user and organisation; a user can be in several organisations. | Role is `Owner`, `Admin` or `Member`. Once an organisation has an Owner, triggers refuse to delete, demote or move its last Owner (`last_owner`). |
| `Invitations` | To one organisation (`OrganizationId`) and one e-mail address; `InvitedByUserId` and `AcceptedByUserId` → `Users`. | Role is `Admin` or `Member`, never `Owner`; e-mail is lower case; only a 64-character lower-case hex SHA-256 of the token fits, so a raw token cannot be stored; tokens are unique; expiry is after creation; accepted and revoked exclude each other; one open invitation per e-mail and organisation. |
| `AuditEvents` | Organisation (optional), actor and target user, type and time. | Foreign keys to `Organizations` and `Users`. |

Nobody becomes a member of any organisation automatically, the legacy organisation included. The start-up check also refuses to start if one of the last-Owner triggers is missing.

Rules that the service layer must enforce when the endpoints arrive, because the database cannot:

- An identity is only created or linked from a token whose signature and issuer have been validated, and two identities are linked only after the user has proved both; never because their e-mail addresses match.
- Accepting an invitation requires a signed-in user whose identity has a verified e-mail equal to the invitation's, and the invitation must not be expired, revoked or accepted. An expired invitation stays "open" for the uniqueness rule until it is revoked, so inviting the same address again first revokes the old invitation.
- Only Owners and Admins of an organisation may invite or change memberships there, and only an Owner may promote someone to Owner. The inviter's and actor's membership is checked in the service, because a database rule would stop members from ever leaving.
- An Owner whose user account is disabled still counts as an Owner for the database rule; the service decides how to treat disabled Owners.
- Audit `Details` must never contain access tokens, invitation tokens or token hashes.

## Local operation and cloud exit (phase 4A)

To start the signed-in .NET API locally, create a verified manual SQLite
backup, or safely restore a cloud snapshot into a **new database file**, use
the PowerShell scripts in `scripts/` and the instructions in
[Local portability and recovery](docs/local-portability.md).

Existing database files are never overwritten by the restore tool. Microsoft
login still requires internet access to Entra. Selecting the server at runtime
inside an installed Android release is a separate mobile feature.

## Database backup and restore

> **Before the first start after updating:** a normal start of the API (`dotnet run`, or starting it from an IDE) **automatically migrates the existing database** it is configured to use, for example `src/MeetingRoomBooking.Api/meeting-room-booking.db`. The first start after the organisation changes rebuilds the `MeetingRooms` and `Bookings` tables. Make your own separate backup first: stop the API and copy the database file, together with any `-wal` and `-shm` files, to a folder outside the repository. The automatic backup described below is an extra safeguard, not a replacement for your own copy.

Before the API applies pending migrations to an existing database, it writes a backup and verifies it: the copy and a fingerprint of every table (row count and content hash) come from one read-transaction snapshot, and the copy must pass `PRAGMA integrity_check` and `PRAGMA foreign_key_check` and match those fingerprints. If the backup cannot be written or verified, the API stops without migrating. After migrating, it checks foreign keys, integrity and the two booking overlap triggers, and refuses to start if any check fails. A brand-new database is not backed up.

If the database file is not empty but has no Entity Framework Core migration history (the `__EFMigrationsHistory` table is missing or empty), the API cannot know which schema the file has. It then stops with an error before migrating or backing up, and leaves the file unchanged so it can be inspected by hand. To start with an empty database instead, point `ConnectionStrings:BookingDatabase` at a new file.

Backups are written to `Database:BackupDirectory`, by default a `backups` folder next to the database file, and named after the first pending migration and the UTC time, for example `meeting-room-booking.pre-20261021065822_CaptureSequenceState-20261021T071023921Z.db`. They are never deleted automatically and are excluded from Git.

To restore a backup:

1. Stop the API so no process has the database open.
2. Move the current `meeting-room-booking.db` and any `-wal` and `-shm` files to a new folder such as `failed-<timestamp>`. Do not delete them.
3. Copy the chosen backup to `meeting-room-booking.db`.
4. Check the copy: `PRAGMA integrity_check` must return `ok`, and the last row of `__EFMigrationsHistory` shows which version the backup belongs to.
5. Start the API version that matches that migration (for a backup taken before phase 1, the version before it). A newer version migrates the restored database again, with a new backup.
6. Check `/health`, `GET /api/rooms` and `GET /api/bookings`.

To try a migration against a copy of a database instead of the original, point the API at the copy explicitly:

```bash
ConnectionStrings__BookingDatabase="Data Source=/path/to/copy/meeting-room-booking.db" \
Database__BackupDirectory="/path/to/copy/backups" \
dotnet run --project src/MeetingRoomBooking.Api --no-launch-profile --urls http://localhost:5203
```

## Date and time handling

The browser form uses `datetime-local`, which represents a local date and time without timezone information.

Before a booking request is sent, Angular converts the local value into an ISO UTC timestamp using `toISOString()`.

The API normalises and stores booking timestamps as UTC values.

When an existing booking is edited, its UTC timestamp is converted back into the local format expected by the `datetime-local` input.

For a larger production system, the organisation or meeting room timezone should also be modelled explicitly.

## Frontend architecture

### Reactive Forms

Angular Reactive Forms manage:

- Current form values.
- Required field validation.
- Maximum text lengths.
- Date range validation.
- Touched and invalid states.
- Create and edit modes.

The form uses a custom validator to ensure that the end time is later than the start time.

Frontend validation improves the user experience, but backend validation remains authoritative.

### Angular Signals

Angular Signals manage local user interface state, including:

- Meeting rooms.
- Bookings.
- Loading state.
- Saving state.
- Editing state.
- Delete confirmation.
- Error messages.
- Success messages.

Computed signals provide derived state, including the current form heading and whether the application is in create or edit mode.

### RxJS Observables

The API service returns RxJS Observables for all HTTP operations.

The application uses:

- `subscribe()` to receive HTTP responses.
- `forkJoin()` to load rooms and bookings in parallel.
- `finalize()` to reset loading and saving states after successful or failed requests.

### API service

HTTP communication is centralised in `BookingApiService`.

The component does not need to know the implementation details of:

- API URLs.
- HTTP methods.
- Request types.
- Response types.

The service supports:

```text
getRooms
getBookings
getBooking
createBooking
updateBooking
deleteBooking
```

### Tailwind CSS

The user interface is styled with Tailwind CSS 4 utility classes.

Tailwind is processed through PostCSS using the configuration in:

```text
src/meeting-room-booking-web/.postcssrc.json
```

The Tailwind import is located in:

```text
src/meeting-room-booking-web/src/styles.css
```

The interface includes responsive layouts for desktop, tablet and mobile screen sizes.

## Prerequisites

Install:

- .NET 10 SDK.
- Node.js 22.
- npm.

The repository contains a `global.json` file that keeps the solution on the .NET 10 SDK family.

Verify the active SDK:

```powershell
dotnet --version
```

The output should start with:

```text
10.0.
```

Verify Node.js:

```powershell
node --version
```

The recommended version is:

```text
v22.
```

## Restore dependencies

From the repository root:

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet restore `
    .\MeetingRoomBooking.sln

npm --prefix `
    .\src\meeting-room-booking-web `
    ci
```

## Run the application

The backend and frontend run as separate development processes.

### Terminal 1 - API

Starting the API migrates its existing database automatically. Before the first start after an update with new migrations, make your own backup of the database file (see [Database backup and restore](#database-backup-and-restore)).

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet run `
    --project .\src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj `
    --launch-profile http
```

The API runs at:

```text
http://localhost:5003
```

Swagger is available at:

```text
http://localhost:5003/swagger
```

### Terminal 2 - Angular frontend

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix `
    .\src\meeting-room-booking-web `
    start
```

Open the application at:

```text
http://localhost:4200
```

The Angular development server proxies requests beginning with `/api` to the backend on port `5003`.

The proxy configuration is located in:

```text
src/meeting-room-booking-web/proxy.conf.json
```

### Other browser clients (CORS)

The Angular app avoids CORS through its proxy. Browser clients served from another origin call the API directly, so the API must allow that origin.

In the Development environment, the API allows the origins listed in `appsettings.Development.json`:

```json
"Cors": {
  "AllowedOrigins": [
    "http://localhost:8081"
  ]
}
```

`http://localhost:8081` is the Expo dev server of the [room-booking-mobile](https://github.com/kris7011/room-booking-mobile) app running in a browser. Only the methods `GET`, `POST`, `PUT` and `DELETE` and the headers `Accept` and `Content-Type` are allowed.

Outside Development no CORS policy is registered, even if `Cors:AllowedOrigins` is set, so the API sends no CORS headers and browsers keep blocking cross-origin requests. Native mobile apps are not affected by CORS.

## Sign-in with Microsoft Entra ID

The API validates Microsoft Entra ID access tokens itself and decides from them who the caller is. A client never tells the API who the user is: a name, e-mail or user ID sent in a request body is never trusted for identity.

Two kinds of Microsoft account can sign in:

- **Work and school accounts** (Microsoft Entra ID) of any tenant, or of the tenants in `Entra:AllowedTenantIds`.
- **Personal Microsoft accounts** (Outlook.com, Hotmail, Live.com), unless `Entra:AllowPersonalAccounts` is `false`. Their tokens come from Microsoft's consumer tenant `9188040d-6c67-4c5b-b112-36a304b66dad` and are validated exactly like work and school tokens, with that tenant as `tid`.

What is in place:

- `GET /api/me` requires a valid access token and returns the caller as the API sees them: display name, `preferred_username`, tenant ID and, once the identity is registered, the API's own user (`publicId`, display name, and the organisations the user belongs to with their role). It is read-only and creates no users or identities.
- `POST /api/me/registration` registers the signed-in identity as a Room Booking user; see [Registration and organisation access](#registration-and-organisation-access).
- Every room and booking endpoint requires a signed-in member of the organisation; see [Organisation access control](#organisation-access-control).
- Without `Entra:ApiClientId` the API starts as before, and `GET /api/me` answers `503` with `code: "authentication_not_configured"`.

### What a token must satisfy

| Check | Rule | Failure |
| --- | --- | --- |
| Signature | RS256, signed with a key from the Microsoft identity platform metadata: `https://login.microsoftonline.com/common/v2.0`, or `…/organizations/v2.0` when personal accounts are off. Only `common` also publishes the keys personal-account tokens are signed with. Unsigned (`alg: none`) and HMAC tokens are rejected. | 401 |
| Signing key issuer | A key that the metadata publishes for a specific issuer may only sign tokens of that issuer (`EnableAadSigningKeyIssuerValidation`). The personal-account keys name the consumer tenant's issuer, so they never validate a work or school token. | 401 |
| Issuer | Exactly `https://login.microsoftonline.com/{tid}/v2.0`, where `{tid}` is the token's own `tid` claim, a GUID; for a personal account `https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0`. Endpoint aliases (`common`, `organizations`, `consumers`) are never an issuer. v1.0 tokens (`https://sts.windows.net/{tid}/`) are rejected, so one person never gets two issuers. | 401 |
| Tenant | The consumer tenant when `Entra:AllowPersonalAccounts` is `true` (the default). Any other tenant, or only those in `Entra:AllowedTenantIds` when it is set. | 401 |
| Audience | The API's client ID or its Application ID URI (`api://{client ID}` by default). | 401 |
| Lifetime | `exp` is required; up to 2 minutes of clock skew. | 401 |
| Delegated scope | `scp` contains `Entra:RequiredScope` (`access_as_user`). App-only tokens (roles, no `scp`) never pass as a user. | 403 |
| User | `tid` and `oid` are present. A linked user whose account is disabled gets `403` with `code: "user_disabled"`. | 403 |

The identity key is the validated `iss` together with `oid`, the same key as the unique index on `UserIdentities (Issuer, Subject)`. `sub` is not used, because it differs between the app's ID token and the API's access token, and the e-mail address is never a key. A personal account and a work account are therefore always different identities, even with the same e-mail address, because their issuers differ.

### Configuration

```json
"Entra": {
  "Instance": "https://login.microsoftonline.com",
  "ApiClientId": "",
  "RequiredScope": "access_as_user",
  "AllowedTenantIds": [],
  "AllowPersonalAccounts": true
}
```

`AllowedTenantIds` limits work and school accounts only; personal accounts are switched on and off with `AllowPersonalAccounts` alone.

None of these values is a secret: the API validates tokens and never requests one, so it has no client secret. Set the API's client ID without editing the file, for example:

```powershell
$env:Entra__ApiClientId = "<Application (client) ID of the API registration>"
dotnet run --project .\src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj --launch-profile http
```

The API stops at start-up if a value is set but invalid (a client ID or tenant ID that is not a GUID, the consumer tenant in `AllowedTenantIds`, an instance that is not HTTPS, or more than one scope).

### Manual steps in the Microsoft Entra admin center

The API needs its own app registration, separate from the mobile app's:

1. **App registrations → New registration**, for example "Room Booking API". Supported account types: *Accounts in any organizational directory and personal Microsoft accounts* (`"signInAudience": "AzureADandPersonalMicrosoftAccount"`). Without personal accounts: *Accounts in any organizational directory*, or *this directory only* together with `Entra:AllowedTenantIds`, and `Entra:AllowPersonalAccounts` set to `false`.
2. **Manifest**: set `"requestedAccessTokenVersion": 2` (in the older manifest format `"accessTokenAcceptedVersion": 2`). Otherwise Entra issues v1.0 tokens, which this API rejects. Entra requires version 2 before personal accounts can be switched on, so for an existing registration set this first, then change `signInAudience` in the manifest.
3. **Expose an API**: set the Application ID URI to `api://<API client ID>` and add the scope `access_as_user` (who can consent: admins and users).
4. **Token configuration → Add optional claim → Access**: `email` and `xms_edov` (accept the prompt to add the Microsoft Graph `email` permission). Only needed for accepting invitations; see [Verified e-mail addresses](#verified-e-mail-addresses).
5. In the **mobile app's** registration: **API permissions → Add a permission → My APIs → Room Booking API → Delegated → access_as_user**, and the same supported account types as the API. Grant admin consent if the tenant does not allow user consent.
6. For users of other tenants: an administrator of each tenant must consent to the mobile app (and with it the API) before its users can sign in. Personal accounts consent for themselves on their first sign-in.

### Not done yet

- Linking a second identity (for example a work account and a personal account) to the same user.
- CORS allows only `Accept` and `Content-Type`, so browser clients cannot send `Authorization` yet; native apps are not affected.

## Organisation access control

Every organisation's rooms and bookings are reached through `/api/organizations/{organizationId}/…`, where `{organizationId}` is the organisation's public ID (a GUID). The ID alone never grants anything. Before any of these endpoints runs, one filter (`OrganizationAccessFilter`) checks, in this order:

| Check | Failure |
| --- | --- |
| A valid access token (see [What a token must satisfy](#what-a-token-must-satisfy)) | `401` |
| The identity is registered | `403 user_not_registered` |
| The user is not disabled | `403 user_disabled` |
| The user is a member of that organisation, and it is not deleted | `404 organization_not_found`: an organisation you are not in looks exactly like one that does not exist |
| The endpoint's role requirement, if it has one | `403 organization_permission_denied` |

Every endpoint, the roles it admits and the tests that prove it are listed in [docs/role-permission-matrix.md](docs/role-permission-matrix.md).

The membership and role are read from the database for every request; nothing in the request body or the token says which organisations someone belongs to. Every query below the filter is limited to the caller's organisation, so a room or booking ID from another organisation is `404 meeting_room_not_found` / `404 booking_not_found`, whether or not it exists. Integer IDs can be guessed, but guessing one never reaches another organisation's data.

| Role | Rooms and bookings |
| --- | --- |
| Owner | Search, book, change and delete every booking of the organisation |
| Admin | As Owner |
| Member | Search and book; change and delete only bookings they made (`403 booking_permission_denied` otherwise) |

A new booking records the signed-in user as its creator (`CreatedByUserId`) and every change the user who made it (`UpdatedByUserId`, `UpdatedUtc`). `bookedBy` defaults to the user's display name when it is left out. Bookings made before sign-in have no creator, so only Owners and Admins can change them. The organisation responses add `createdByCurrentUser` and `canModify`, so a client can show or hide its edit and delete actions; the API checks every change itself regardless.

The existing booking rules are unchanged: validation, the start in the future, the overlap triggers and `409 booking_overlap`. Booking changes now run in one `BEGIN IMMEDIATE` transaction each, so checking the room and writing the booking cannot be interleaved with another writer.

### Creating organisations

Any registered, enabled user can create an organisation and becomes its Owner; a user can be a member of any number of organisations. The legacy organisation and its rooms and bookings are untouched by this.

```http
POST /api/organizations
Authorization: Bearer <access token>
Idempotency-Key: 3f2a7c1e-9b4d-4e8a-a1f0-6c5d2b7e9a13
Content-Type: application/json

{ "name": "Acme Robotics" }
```

- The name is trimmed, runs of white space become one space, and it must then have 2 to 100 characters without control or formatting characters (`400` otherwise).
- `Idempotency-Key` is required: a new random GUID for every new organisation, and the same one when a request is repeated (a retry after a timeout, a double tap). The same key with the same name returns the organisation it created, `200` with `Idempotent-Replayed: true`, instead of `201`; with another name it is `422 idempotency_key_reused`. Keys belong to their user. The unique index `UX_Organizations_CreatedByUserId_CreationRequestId` decides concurrent repeats.
- The organisation (with a new random public ID), the Owner membership and the `OrganizationCreated` and `MembershipAdded` audit events are written in one `BEGIN IMMEDIATE` transaction: there is never an organisation without its Owner.
- A user can create at most 10 organisations in 24 hours (`429 organization_creation_limit`); repeats do not count.

`GET /api/organizations` lists the caller's organisations with their role. `GET /api/organizations/{id}` returns one of them with the member and active room counts, and Owners can rename it with `PUT` (`OrganizationRenamed` audit event). The legacy organisation keeps its name (`409 legacy_organization_read_only`).

### Meeting rooms

A new organisation has no rooms. Owners and Admins create and manage them; Members only search and book (`403 organization_permission_denied`).

| Endpoint | Rules |
| --- | --- |
| `POST …/rooms` with `{ "name", "capacity" }` | Name 1–100 characters after trimming, capacity 1–500. Names are unique among the organisation's active rooms, ignoring case (`409 room_name_taken`); other organisations may use the same name. |
| `PUT …/rooms/{id}` | The same rules. An archived room is restored before it is changed (`409 room_archived`). Its bookings keep pointing to it. |
| `POST …/rooms/{id}/archive` | Rooms are never deleted. Archiving hides the room from the room list, the availability search and new bookings, and keeps every booking. Refused while the room has bookings that have not ended (`409 room_has_upcoming_bookings`), so none silently loses its room. Archiving twice changes nothing. |
| `POST …/rooms/{id}/restore` | Makes the room bookable again, unless an active room has taken its name meanwhile (`409 room_name_taken`). |
| `GET …/rooms?includeArchived=true` | Owners and Admins only. |

Every change writes a `RoomCreated`, `RoomUpdated`, `RoomArchived` or `RoomRestored` audit event. A room of another organisation is `404 meeting_room_not_found`.

### The original endpoints

`/api/rooms` and `/api/bookings` keep their URLs, request and response shapes and booking IDs, and always work on the legacy organisation. They are protected exactly like `/api/organizations/{legacy organisation}/…`: a signed-in, registered member of the legacy organisation, whose role decides what they may change. Without a token they answer `401`; a registered user who is not a member gets `404 organization_not_found`.

For a local demo without sign-in they can be made anonymous again, as before:

```json
"LegacyApi": {
  "AllowAnonymous": true
}
```

or `$env:LegacyApi__AllowAnonymous = "true"`. This is only accepted in the `Development` and `Testing` environments: in any other environment the API refuses to start with it, so real organisation data is never anonymous in production. In demo mode a token sent to these endpoints is ignored, and the organisation endpoints still require sign-in. The default, in `appsettings.json`, is `false`. The Angular frontend in this repository calls the original endpoints without a token, so it needs demo mode.

## Registration and organisation access

### Registration

`POST /api/me/registration` (no request body) creates the API's own user for the signed-in identity:

- The identity key is the validated `iss` together with `oid` (`UserIdentities (Issuer, Subject)`). Nothing in the request body is read, and an e-mail address is never a key: two identities with the same address become two users.
- `201 Created` with `created: true` for the request that created the user; `200 OK` with `created: false` and the same user every time after that. Nothing is created twice.
- Safe under concurrent requests: the lookup and the inserts run in one `BEGIN IMMEDIATE` transaction, so two first sign-ins of the same person are serialised. If another writer still inserts the identity first, the unique-index violation is answered with the user that writer created.
- A disabled user gets `403` with `code: "user_disabled"`, and nothing is changed.
- Registration grants nothing: a new user is a member of no organisation, the legacy organisation included. It writes a `UserRegistered` audit event and logs the new user's public ID (never a token or an e-mail address).
- The response body is `{ created, currentUser }`, where `currentUser` is what `GET /api/me` returns.

`GET /api/me` stays read-only. The mobile app calls it first and registers only when `user` is `null`.

### Verified e-mail addresses

The API treats the `email` claim as verified only when the token also carries `xms_edov: true`. Microsoft documents `email` on its own as unverified and mutable, so without `xms_edov` the identity has no verified e-mail. `preferred_username` is never used as an e-mail. A verified e-mail is only ever compared with the address of an invitation, together with the invitation's secret token; it never grants access on its own.

To get these claims, add the **optional claims** `email` and `xms_edov` to the **access token** of the API's app registration (step 4 under the admin-center steps above). Without them, registration still works, but invitations cannot be accepted (`verified_email_required`).

### The first Owner of the legacy organisation

The legacy organisation was created by a migration and has no creator. Someone who controls the API's configuration therefore names its first Owner:

1. The person signs in with the app once, which registers them. The API logs `Registered user {UserPublicId} …`, and the app shows the same ID as "Bruger-ID".
2. Set `Organizations:InitialLegacyOwnerUserId` to that public ID and restart the API:

   ```powershell
   $env:Organizations__InitialLegacyOwnerUserId = "<user public ID>"
   ```

3. At start-up the API makes that user the Owner of the legacy organisation, writes a `MembershipAdded` audit event with no actor (the system), and logs it.

The setting takes effect only while the legacy organisation has no Owner. Once it has one, the setting changes nothing: a different user is logged and ignored, never added. Further members join by invitation. A user who has not registered yet, or is disabled, is logged and skipped. A value that is not a GUID stops the API at start-up. The API generates the ID at registration, so nobody can choose or claim it from a client, and no e-mail address is involved.

### Invitations

| Endpoint | Who | Rules |
| --- | --- | --- |
| `POST /api/organizations/{organizationId}/invitations` with `{ "email", "role" }` | A registered, enabled Owner or Admin of the organisation | Owners invite `Admin` or `Member`, Admins only `Member`; `Owner` is never invited (`400`). Not a member: `404 organization_not_found`, so an organisation's existence is not revealed. A member without the right: `403 organization_permission_denied`. Inviting the same address again revokes the earlier invitation. Returns `201` with the token, shown only here; the database keeps its SHA-256 hash. Valid for 7 days. |
| `POST /api/invitations/accept` with `{ "token" }` | A registered, enabled user | The token must match an open, unexpired invitation (`404 invitation_not_found`, `410 invitation_expired`, `410 invitation_no_longer_valid`). The caller's token must carry a verified e-mail (`403 verified_email_required`) equal to the invited address (`403 invitation_email_mismatch`), and the inviter must still be allowed to invite that role. Already a member: `409 already_member`. Accepting the same invitation again returns the same membership. |

| `GET /api/organizations/{organizationId}/invitations` | Owners and Admins | Open invitations (not accepted or revoked), expired ones included with `isExpired: true`, with who sent them and `canRevoke`. Never the token or its hash. |
| `DELETE /api/organizations/{organizationId}/invitations/{id}` | Owners; Admins only for `Member` invitations | Revokes an open invitation, so its link stops working (`410 invitation_no_longer_valid` on accepting). Not open, or another organisation's: `404 invitation_not_found`. |

Neither the e-mail address nor the token is enough alone: the token proves the link reached someone, and the verified e-mail proves it is the person it was sent to. Both endpoints answer `403 user_not_registered` to a signed-in identity that has not registered. Every change writes audit events without tokens or token hashes.

### Members and roles

| Role | Organisation administration |
| --- | --- |
| Owner | Everything: rooms, invitations (Admin and Member), member list, role changes (Owner included), removing members, renaming the organisation |
| Admin | Rooms, the member list, inviting Members and revoking Member invitations. Never changes roles, so an Admin can never make anyone, themselves included, an Owner |
| Member | Searching and booking. Can leave the organisation |

- `GET …/members` returns `userPublicId`, `displayName`, `role`, `joinedUtc` and `isCurrentUser`, Owners first.
- `PUT …/members/{userId}/role` with `{ "role": "Owner" | "Admin" | "Member" }`, Owners only. Ownership is handed over by making someone else an Owner, never by invitation.
- `DELETE …/members/{userId}`: Owners remove anyone; any member may remove themselves (leave). Their bookings stay. Invitations they sent stop working once they may no longer invite.
- The last Owner can be neither demoted, removed nor leave (`409 last_owner`). The service checks it, and the database triggers `TR_Memberships_LastOwner_*` refuse it as well. Both changes run in a `BEGIN IMMEDIATE` transaction that reads the caller's role again, so two Owners removing each other at the same time leave one Owner.
- A user who is not a member is `404 member_not_found`. Every change writes a `MembershipRoleChanged` or `MembershipRemoved` audit event (`reason: removed` or `left`).

## Build and test

### Backend

Restore and build the solution:

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet restore `
    .\MeetingRoomBooking.sln

dotnet build `
    .\MeetingRoomBooking.sln `
    --configuration Release `
    --no-restore
```

Run the backend test suite:

```powershell
dotnet test `
    .\MeetingRoomBooking.sln `
    --configuration Release `
    --no-build
```

The backend suite consists of integration tests against the real application and temporary SQLite databases; see [Testing strategy](#testing-strategy).

### Frontend

Install the locked frontend dependencies:

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix `
    .\src\meeting-room-booking-web `
    ci
```

Create a production build:

```powershell
npm --prefix `
    .\src\meeting-room-booking-web `
    run build
```

Run the frontend tests:

```powershell
npm --prefix `
    .\src\meeting-room-booking-web `
    test -- --watch=false
```

The frontend suite currently contains 8 tests:

- 6 API-service tests.
- 2 application-component tests.

## Testing strategy

### Backend tests

Backend integration tests start the real ASP.NET Core application through `WebApplicationFactory`.

Each test factory uses an isolated temporary SQLite database.

Connection pooling is disabled in the test configuration so Windows can reliably release and delete the temporary database file after each test.

Backend scenarios include:

- Database migration and seeded meeting rooms.
- Retrieval of meeting rooms.
- Retrieval of bookings.
- Retrieval of a booking by identifier.
- Valid booking creation.
- Booking persistence.
- Overlap rejection.
- The same period in different rooms.
- Adjacent bookings.
- Invalid time ranges.
- Unknown meeting rooms.
- Valid updates.
- Updates without self conflict.
- Conflicting updates.
- Unknown bookings.
- Successful deletion.
- Deletion of missing bookings.
- CORS: the Expo web origin is allowed in Development, other origins are not, and Production sends no CORS headers.
- Environment defaults: Swagger only in Development, HTTP redirected to HTTPS outside Development when the HTTPS port is known, and an unhandled exception answered with a generic 500 problem without its details in every environment.
- Migrations: a database on the previous schema keeps every room, booking, ID and UTC timestamp; the AUTOINCREMENT counters never go back; both overlap triggers survive; organisation keys and indexes hold.
- Backup and restore: a verified backup before pending migrations, start-up stops when the backup fails or when a non-empty database has no migration history, and a real restore of a backup file.
- Organisation scope: the existing endpoints never show or change data of another organisation, and their JSON is unchanged.
- Identities, memberships, invitations and audit events: unique keys, foreign keys, check constraints, the last-Owner triggers, memberships across several organisations, and a migration from the schema of phase 1, PR 1 that keeps every room, booking and overlap trigger.
- Registration: first registration, repeated registration, 12 parallel first registrations (exactly one user), an existing and a disabled user, invalid and app-only tokens and a missing scope (nothing created), `GET /api/me` creating nothing, the same `oid` in another tenant or as a personal account and the same e-mail on two identities (always separate users), and when an e-mail counts as verified.
- Organisation access: the configured first legacy Owner (at start-up, never replacing an existing Owner, skipping unknown and disabled users, refusing a value that is not a user ID); invitations only by Owners and Admins (Admins only Members, never Owner); no access without membership; acceptance only with the token and the matching verified e-mail; expiry, revocation by re-inviting, an inviter who lost the right, disabled users, existing members and parallel accepts; no tokens in the database or the audit log.
- Sign-in: `GET /api/me` with tokens signed by a test key that exists only in the test process (no network, no real tenant): a valid token, both audiences, a linked and a disabled user, the same `oid` in another tenant, and rejection of a missing token, a wrong audience, issuer or tenant, a v1.0 issuer, an expired, unsigned, HMAC-signed or foreign-key-signed token, a signing key published for another tenant, a missing scope and an app-only token. Invalid `Entra` settings stop the API at start-up.

Migration tests build their databases with raw SQL in their own temp folders. A guard stops any test from opening a database outside the system temp folder, and the test host receives its connection string explicitly, so tests can never touch the development database.

### Frontend service tests

Frontend API service tests verify:

- Request URL.
- HTTP method.
- Request body.
- Returned response.
- Completion of delete requests.
- Absence of unexpected HTTP requests.

The tests use Angular's HTTP testing utilities rather than starting the real backend.

### Frontend component tests

Application component tests verify:

- That the Angular application can be created.
- That rooms and bookings are loaded.
- That existing bookings are rendered.
- That selecting Edit populates the booking form.

## Continuous integration

GitHub Actions runs automatically for:

- Pushes to `main`.
- Pull requests.

The workflow runs backend and frontend validation as separate jobs.

```text
Backend:
restore => Release build => integration tests

Frontend:
npm ci => production build => unit tests
```

The workflow is located at:

```text
.github/workflows/ci.yml
```

Separating the jobs makes it clear whether a failure belongs to the backend or frontend.

## Design decisions

### SQLite rather than an in-memory collection

SQLite provides persistent data, migrations, constraints and real database queries without requiring a separate database server.

It also allows integration tests to use the same database provider as the running application.

### Feature oriented backend structure

Booking entities, contracts, endpoints and services are grouped together.

This makes the code for each feature easier to locate and understand.

### Backend owned business rules

The frontend performs validation to improve the user experience, but the API remains authoritative for validity and overlap rules.

This prevents business rules from being bypassed by another frontend or API client.

### Read-only EF Core queries

Queries that materialise entities only for display use `AsNoTracking()`.

This avoids unnecessary EF Core change tracking overhead.

Update and delete operations use tracked entities so EF Core can detect changes and generate the required SQL statements when `SaveChangesAsync()` is called.

### Problem Details responses

Expected API errors use structured Problem Details responses with application specific error codes.

The frontend can therefore distinguish between overlap conflicts, missing resources and validation errors.

### Angular API service

HTTP calls are centralised in `BookingApiService`.

Angular components do not need to know endpoint addresses or HTTP implementation details.

### Local list updates

After successful create, update and delete operations, the frontend updates its local booking signal directly.

This avoids issuing an additional full GET request after every mutation.

### Tailwind utility classes

Most visual styling is expressed through Tailwind utility classes close to the associated HTML.

The global stylesheet is limited to the Tailwind import and a small number of application wide browser styles.

### UTC storage

Booking times are stored as UTC values to avoid tying stored data to the timezone of the machine running the API.

## Current test results

CI runs every test on each pull request and push to `main`; see the [Actions runs](https://github.com/kris7011/meeting-room-booking/actions). The last locally verified counts are recorded in [docs/PROJECT_WALKTHROUGH.md](docs/PROJECT_WALKTHROUGH.md#9-testing-strategy).

## Possible improvements

With more development time, the following could be added:

- Calendar style schedule view.
- Filtering by room or date.
- Recurring bookings.
- Pagination.
- An endpoint for reading audit events (they are recorded, but not exposed).
- Optimistic concurrency handling.
- More advanced timezone modelling.
- End-to-end browser tests for the Angular client (the mobile repository has Playwright tests against this API).
- Docker support.
- Deployment configuration.
- Structured telemetry.