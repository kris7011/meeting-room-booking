# Testing and CI

What the backend and the Angular client test, how to run it, and what CI
runs. The mobile app's end-to-end tests also run against this API; see
[room-booking-mobile/docs/testing.md](https://github.com/kris7011/room-booking-mobile/blob/main/docs/testing.md).

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
- Role and permission matrix: `RolePermissionMatrixTests` reads the API's route table and fails if an endpoint's role requirement differs from [role-permission-matrix.md](role-permission-matrix.md).
- Changes under the write lock: `MutationReauthorizationTests` holds a request between its access check and its transaction (`TransactionGate`, no sleeps) while the caller is removed, demoted or disabled, and proves nothing is written ([ADR 0004](adr/0004-reauthorize-mutations-under-the-write-lock.md)).
- Idempotent booking creation: repeats, a key reused with other details, a deleted booking, keys scoped to user and organisation, retries after revoked access ([ADR 0005](adr/0005-idempotent-booking-creation.md)).
- Booking privacy: Members see other people's bookings without title and booked-by, Owners and Admins in full, a role change applies to the next request, and the original endpoints keep their shape (`BookingPrivacyTests`, [ADR 0006](adr/0006-booking-details-hidden-from-members.md)).
- The documented journey as one story (`DocumentedJourneyTests`): registration, organisation creation with a retried key, rooms, invitations, search, booking, overlap, privacy, role rules, last Owner, archiving, tenant isolation and removal.
- Sign-in: `GET /api/me` with tokens signed by a test key that exists only in the test process (no network, no real tenant): a valid token, both audiences, a linked and a disabled user, the same `oid` in another tenant, and rejection of a missing token, a wrong audience, issuer or tenant, a v1.0 issuer, an expired, unsigned, HMAC-signed or foreign-key-signed token, a signing key published for another tenant, a missing scope and an app-only token. Invalid `Entra` settings stop the API at start-up.

Migration tests build their databases with raw SQL in their own temp folders. A guard stops any test from opening a database outside the system temp folder, and the test host receives its connection string explicitly, so tests can never touch the development database.

### Angular client tests

Vitest through the Angular CLI (`npm test -- --watch=false`), 31 tests:

- `BookingApiService` and `AccountApiService` paths, methods and bodies with Angular's `HttpTestingController`, for the original endpoints and an organisation scope.
- `parseAuthConfig`: a missing or invalid `auth-config.json` means "sign-in unavailable".
- `AuthService` against a fake MSAL client (no network): unavailable without settings, sign-in by redirect with the API scope, a returning sign-in, silent token renewal, interaction required, sign-out, cancelled sign-in.
- `authInterceptor`: the token goes only to relative `/api/` paths, never to an absolute or protocol-relative URL, and not while signed out.
- The page: demo mode, editing, the signed-in organisation flow, first-visit registration, no organisation, hidden booking details without actions, and a late answer from an organisation the user switched away from.

Not automated: a real Microsoft sign-in in a browser. It was checked by hand
once, below.

### Manual browser sign-in check (2026-10-29)

Setup: the Angular client (`npm start`, `http://localhost:4200`) with
`public/auth-config.json` pointing to its own SPA registration
(`RoomBooking-Web-Dev`, redirect URI `http://localhost:4200`, delegated
`access_as_user` on the API registration, work, school and personal
accounts). The API ran through `scripts/Start-Local.ps1` with
`Entra__ApiClientId` set, on a *new* database restored from a fresh verified
backup, so the development database was not opened. Browser: Microsoft Edge
with a new, empty profile. The owner typed the Microsoft sign-in, consent and
sign-out choices by hand; the booking steps and the reload were then driven
through the page with Playwright over CDP. Account: the owner's personal
Microsoft account, an existing Room Booking user and Owner of two
organisations in that copy.

| Step | Observed |
| --- | --- |
| Signed out | Demo-mode notice and **Sign in with Microsoft**; the legacy endpoints answer `401` because the API is not in demo mode, and the page says so. |
| Sign-in by redirect, first consent to the web registration | Back on the app: `GET /api/me` `200`, `GET /api/organizations` `200`; both organisations listed with role Owner; rooms and bookings of the first one `200`. |
| Switch organisation | Rooms and bookings of the other organisation `200`. |
| Create, change, delete a synthetic booking through the form | `POST` `201`, listed; `PUT` `200`, new title listed; `DELETE` `204`, removed from the list and from the database copy. |
| Reload the page | Still signed in without a Microsoft prompt (token from `sessionStorage`), `GET /api/me` `200`. |
| **Sign out** | Redirect to Microsoft's sign-out, which asks which account to sign out of; back on the app in demo mode, with no access, refresh or ID token left in `sessionStorage`. |
| Sign in again | `GET /api/me` and the organisation calls `200`. |

Not covered by this check: a work or school account, a first-time user
(`POST /api/me/registration`), the Member view with hidden details (the
signed-in user was Owner everywhere), renewal after the access token expires
(about an hour), and a production build.

## Build and test

### Backend

Restore and build the solution:

```powershell
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

The frontend suite contains 31 tests (Vitest), run in CI:

- API services: request URLs, methods and bodies, for the legacy and the organisation scope.
- Sign-in: the settings file, `AuthService` against a fake MSAL client, and the interceptor (token only to the app's own `/api/` paths).
- The page: demo mode, the signed-in organisation flow, hidden booking details, and a late answer from an organisation the user switched away from.

## Continuous integration

GitHub Actions runs automatically for:

- Pushes to `main`.
- Pull requests.

The workflow runs backend and frontend validation as separate jobs.

```text
Backend:
restore => Release build => database tool build => integration tests
=> demo script smoke test (scripts/Start-Demo.ps1 -Reset -StopAfterSeed, pwsh on Linux)

Frontend:
npm ci => production build => unit tests
```

The workflow is located at:

```text
.github/workflows/ci.yml
```

Separating the jobs makes it clear whether a failure belongs to the backend or frontend.
