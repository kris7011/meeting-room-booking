# Room Booking – Developer Walkthrough (API and web)

This document explains the system from a developer's perspective: what it
does, how a request travels through it, why it is built this way, and where
its limits are. It describes the code as it is, and links to the source.

The mobile client has its own walkthrough in
[room-booking-mobile/docs/PROJECT_WALKTHROUGH.md](https://github.com/kris7011/room-booking-mobile/blob/main/docs/PROJECT_WALKTHROUGH.md).

**Status labels used throughout**

| Label | Meaning |
| --- | --- |
| **[Tested]** | Implemented and covered by automated tests that run in CI. |
| **[Manual]** | Implemented; verified only by hand, or not verified at all. |
| **[In review]** | Implemented on an open pull request, not yet on `main`. |
| **[Not implemented]** | Proposed or future work. Nothing in the code does this yet. |

Last reviewed: 2026-10-28, after PRs #19 (booking privacy), #20 (Angular
patches), #21 (Angular sign-in) and #22 (journey test, demo script) were
merged into `main`.

---

## 1. System overview

Room Booking lets organisations manage meeting rooms and lets their members
find a free room and book it. People sign in with a Microsoft account. Every
organisation's rooms and bookings are visible only to its members.

```mermaid
flowchart LR
    phone["Android app<br/>(Expo, room-booking-mobile)"]
    web["Angular web app<br/>(src/meeting-room-booking-web)"]
    entra["Microsoft Entra ID<br/>(issues access tokens)"]
    api["ASP.NET Core API<br/>(src/MeetingRoomBooking.Api)"]
    db[("SQLite file<br/>meeting-room-booking.db")]

    phone -- "sign-in (PKCE)" --> entra
    phone -- "HTTPS, or HTTP to localhost via USB<br/>Bearer access token" --> api
    web -- "sign-in (PKCE, redirect)" --> entra
    web -- "signed in: /api/organizations/…<br/>Bearer token, same origin via proxy<br/>signed out: /api/rooms, /api/bookings<br/>no token (demo mode only)" --> api
    api -- "signing keys (OpenID metadata)" --> entra
    api -- "EF Core" --> db
```

- The **API** is the only component that decides who a caller is and what
  they may do. Clients are conveniences.
- **Microsoft Entra ID** authenticates people. The API never sees a password
  and has no client secret: it only validates tokens.
- **SQLite** is a single file. That keeps the system runnable on one PC
  without a database server, which matters for the
  [local-operation goal](#11-backup-and-restoration).

## 2. Repository structure

```text
src/MeetingRoomBooking.Api/
  Program.cs                      Composition root: services, middleware, endpoint groups, start-up
  Data/
    BookingDbContext.cs           EF Core model: tables, keys, check constraints, indexes
    Migrations/                   Schema history (EF Core), including SQL triggers
    DatabaseInitializer.cs        Start-up: backup → migrate → verify invariants
    DatabaseBackup.cs             Verified backups: one snapshot, copied and fingerprinted
    DatabasePortability.cs        Manual backup and restore-to-new-file   [Tested]
  Features/                       One folder per feature: entity, contracts, endpoints, service
    Authentication/               Entra token validation
    Users/                        Signed-in identity, /api/me, registration
    Organizations/                Organisations, memberships, invitations, access control
    Rooms/                        Rooms and room administration
    Bookings/                     Bookings
    Audit/                        Audit event entity
    ApiProblems.cs                Problem Details with stable error codes
src/meeting-room-booking-web/     Angular 21 client: demo mode, or Microsoft sign-in and organisation bookings
tests/MeetingRoomBooking.Api.IntegrationTests/
tools/RoomBooking.DatabaseTool/   CLI for backup/restore                  [Manual]
scripts/                          Start-Local, Backup-Local, Restore-Local [Manual]
```

The backend is **feature-oriented**: everything about bookings lives in
`Features/Bookings`, rather than spread over `Controllers/`, `Services/` and
`Models/` folders. A reader who wants to understand bookings opens one folder.

## 3. Technologies and their responsibilities

| Technology | Responsibility here |
| --- | --- |
| .NET 10, ASP.NET Core Minimal APIs | HTTP endpoints, endpoint groups and filters, dependency injection |
| Microsoft.AspNetCore.Authentication.JwtBearer + Microsoft.IdentityModel | Validating Entra access tokens (signature, issuer, audience, lifetime) |
| Entity Framework Core 10 + SQLite provider | Data access, migrations, LINQ queries translated to SQL |
| SQLite | The database: one file, with check constraints, foreign keys, triggers |
| xUnit + `WebApplicationFactory` | Integration tests against the real application and a temporary database |
| Angular 21, Signals, Reactive Forms, Tailwind CSS 4, Vitest | The web client |
| GitHub Actions | CI: backend build and tests, database tool build, frontend build and tests |
| PowerShell | Local start, backup and restore scripts for Windows [Manual] |

## 4. How a request flows through the API

[`Program.cs`](../src/MeetingRoomBooking.Api/Program.cs) wires the pipeline in
this order:

1. `UseExceptionHandler` – unhandled exceptions become Problem Details.
2. `UseCors` – only in Development, only for origins in
   `appsettings.Development.json`.
3. `UseAuthentication` / `UseAuthorization` – JWT bearer validation.
4. Swagger (Development only); HTTPS redirection (outside Development and Testing).
5. Endpoint groups, each with its own authorization and filters.

Before the app accepts requests it runs `InitialiseDatabaseAsync` (backup,
migrate, verify; see [section 7](#7-persistence)) and
`ApplyLegacyOwnerBootstrapAsync` (see [section 8.5](#85-the-first-owner-of-the-legacy-organisation-tested)).

### Example: a member books a room

```mermaid
sequenceDiagram
    participant C as Client
    participant J as JwtBearer + SignedInUserPolicy
    participant F as OrganizationAccessFilter
    participant E as Booking endpoint
    participant S as BookingService
    participant D as SQLite

    C->>J: POST /api/organizations/{id}/bookings<br/>Authorization: Bearer …
    J->>J: signature, issuer, audience, lifetime, scp, tid, oid
    J-->>C: 401 / 403 if invalid
    J->>F: validated ClaimsPrincipal
    F->>D: user by (iss, oid); membership of {id}
    F-->>C: 403 not registered/disabled, 404 not a member
    F->>E: CurrentOrganization.Caller set
    E->>E: BookingRequestValidator.Validate → 400
    E->>S: CreateAsync(organizationId, request, actor)
    S->>D: BEGIN IMMEDIATE
    S->>D: active room in this organisation? overlap?
    S->>D: INSERT booking (trigger re-checks overlap)
    S->>D: COMMIT
    S-->>E: booking, or a failure enum
    E-->>C: 201, or 400 / 404 / 409 Problem Details
```

Two design points are visible here:

- **Authorization runs before the endpoint.** The organisation filter is
  attached to the route *group*, so no endpoint in the group can forget it.
- **Services return result enums, not HTTP results.** `BookingService`
  returns `BookingMutationFailure` values;
  `BookingEndpoints.HandleMutationFailure` maps them to status codes. The
  business logic does not depend on HTTP, and the mapping is in one place.

## 5. Authentication [Tested]

Code: [`EntraAuthentication.cs`](../src/MeetingRoomBooking.Api/Features/Authentication/EntraAuthentication.cs),
[`EntraIssuerValidator.cs`](../src/MeetingRoomBooking.Api/Features/Authentication/EntraIssuerValidator.cs),
[`SignedInIdentity.cs`](../src/MeetingRoomBooking.Api/Features/Users/SignedInIdentity.cs).

The API is a **resource server**: clients obtain Entra v2.0 access tokens and
send them as `Authorization: Bearer`. `AddEntraAuthentication` configures JWT
bearer validation:

| Check | How |
| --- | --- |
| Signature | RS256 only (`ValidAlgorithms`), keys from Entra's OpenID metadata (`common`, or `organizations` when personal accounts are off). `RequireSignedTokens`. |
| Signing key belongs to the issuer | `EnableAadSigningKeyIssuerValidation()` |
| Issuer | `EntraIssuerValidator.Validate`: `iss` must equal `https://login.microsoftonline.com/{tid}/v2.0` for the token's own `tid`; allowed tenants are configurable; v1.0 issuers are rejected. |
| Audience | The API's client ID or `api://{client ID}` |
| Lifetime | `exp` required, 2 minutes clock skew |
| Delegated user | Policy `SignedInUserPolicy` requires `tid`, `oid` and the `access_as_user` scope in `scp`, so app-only tokens never pass as a user. |

**Identity key.** A person is identified by the validated issuer plus `oid`
(object ID), stored as a unique index on `UserIdentities (Issuer, Subject)`.
The e-mail address is never a key: two Microsoft accounts with the same
address remain two users.

**Verified e-mail.** `SignedInIdentity.ReadVerifiedEmail` trusts the `email`
claim only when the token also carries `xms_edov: true`. A verified e-mail is
used for exactly one thing: matching an invitation's address.

**Not configured.** Without `Entra:ApiClientId` the API still starts; signed-in
endpoints answer `503 authentication_not_configured`.

**E-mail one-time code (Microsoft Entra External ID)** [Tested with test
tokens; not run against Microsoft]. Code:
[`ExternalIdAuthentication.cs`](../src/MeetingRoomBooking.Api/Features/Authentication/ExternalIdAuthentication.cs).
When the `ExternalId` section is configured, a second JWT bearer scheme
validates tokens of one external tenant against that tenant's own metadata
(issuer, keys), its own audience and its `tid`. The default scheme
`SignedIn` is a policy scheme that forwards a token to `ExternalId` when its
unvalidated `iss` is a `ciamlogin.com` URL and to Entra's `Bearer`
otherwise; the chosen scheme does all validation. `SignedInIdentity.Provider`
comes from the scheme that validated the token, and `GET /api/me` returns it
as `provider`. External ID identities have no verified e-mail unless
`ExternalId:EmailClaimIsVerified` is on, so they cannot accept invitations
by default. Why: [ADR 0008](adr/0008-external-id-as-a-second-token-issuer.md);
set-up and the owner's steps: [external-id-setup.md](external-id-setup.md).

**What the tests do not cover:** tests sign tokens with a key that exists only
in the test process ([`EntraTestTokens.cs`](../tests/MeetingRoomBooking.Api.IntegrationTests/Authentication/EntraTestTokens.cs)).
Validation against Microsoft's real metadata and real tokens is **[Manual]**.

## 6. Authorization and multi-tenancy [Tested]

Code: [`OrganizationAccess.cs`](../src/MeetingRoomBooking.Api/Features/Organizations/OrganizationAccess.cs),
[`LegacyApiAccess.cs`](../src/MeetingRoomBooking.Api/Features/Organizations/LegacyApiAccess.cs).

Every organisation endpoint is mapped inside `MapOrganizationGroup`, under
`/api/organizations/{organizationId:guid}`. The group requires
`SignedInUserPolicy` and runs `OrganizationAccessFilter` before every endpoint:

| Step | Failure |
| --- | --- |
| Valid token (policy) | 401 / 403 |
| Identity is registered | 403 `user_not_registered` |
| User is not disabled | 403 `user_disabled` |
| Caller is a member of the organisation in the route | **404** `organization_not_found` |
| Endpoint's role requirement (`RequireOwnerOrAdmin`, `RequireOrganizationRole`) | 403 `organization_permission_denied` |

- **404, not 403, for non-members:** an outsider cannot even tell whether an
  organisation exists.
- **Roles come from the database on every request**, never from the token or
  the request body.
- **Queries are scoped as well.** For example, `BookingService.BookingsOf`
  filters by `OrganizationId`, so a booking ID from another organisation is
  "not found" even if it exists. Integer IDs are guessable; guessing one never
  crosses the tenant boundary.
- **Per-object rules** live in the service: `BookingActor.MayModify` lets
  Owners and Admins change any booking, Members only their own.
- **Booking details are private to Owners, Admins and the booker.** Every
  member sees which room is booked when, but `BookingActor.MaySeeDetails`
  removes the title and "booked by" from another person's booking before a
  Member gets it (`detailsHidden: true`). Anonymous demo mode shows
  everything ([ADR 0006](adr/0006-booking-details-hidden-from-members.md)).
- **Changes check again under the write lock.** The filter's `OrganizationCaller`
  is a snapshot from the start of the request. Every organisation change
  (rooms, bookings, rename, invitation revoke, roles, removal) first calls
  `OrganizationAccessService.ReauthorizeAsync` inside its `BEGIN IMMEDIATE`
  transaction, and the per-object rules use the role read there. If the caller
  was disabled, removed or demoted meanwhile, it throws
  `OrganizationAccessDeniedException`; the transaction rolls back and the
  filter answers exactly as it would have for a later request
  ([ADR 0004](adr/0004-reauthorize-mutations-under-the-write-lock.md)).

| Role | Rooms and bookings | Administration |
| --- | --- | --- |
| Owner | Book; see, change and delete any booking | Rooms, invitations (Admin, Member), roles, remove members, rename |
| Admin | As Owner | Rooms, member list, invite Members, revoke Member invitations |
| Member | Search and book; change/delete own bookings; others' bookings without title and booked-by | Leave the organisation |

The full endpoint-by-role matrix, with the test that proves each cell, is in
[role-permission-matrix.md](role-permission-matrix.md). `RolePermissionMatrixTests`
reads the route table and fails if an endpoint's role requirement no longer
matches it.

**Legacy endpoints.** `/api/rooms` and `/api/bookings` are the original
assignment endpoints. They always work on the legacy organisation and are
protected by the same filter with a fixed organisation ID. `LegacyApi:AllowAnonymous`
re-enables anonymous access for demos, and `LegacyApiOptions.Read` **refuses
to start the API** with that setting outside the Development and Testing
environments.

## 7. Persistence

Code: [`BookingDbContext.cs`](../src/MeetingRoomBooking.Api/Data/BookingDbContext.cs),
[`Migrations/`](../src/MeetingRoomBooking.Api/Data/Migrations),
[`DatabaseInitializer.cs`](../src/MeetingRoomBooking.Api/Data/DatabaseInitializer.cs).

### Tables

`Organizations`, `MeetingRooms`, `Bookings`, `Users`, `UserIdentities`,
`Memberships`, `Invitations`, `AuditEvents`, `BookingCreationRequests`
(Idempotency-Keys of booking creations, kept 24 hours; [ADR 0005](adr/0005-idempotent-booking-creation.md)),
plus EF Core's `__EFMigrationsHistory`.

### Invariants enforced by the database [Tested]

The services check the business rules first, so users get good error messages.
The database enforces the critical ones again, so a bug, a second code path or
a manual SQL statement cannot break them:

| Invariant | Mechanism |
| --- | --- |
| No two bookings of a room overlap | Triggers `TR_Bookings_Overlap_Insert/Update` (`RAISE(ABORT, 'booking_overlap')`) |
| A booking belongs to its room's organisation | Composite foreign key `(OrganizationId, RoomId)` → `MeetingRooms (OrganizationId, Id)` |
| An organisation never loses its last Owner | Triggers `TR_Memberships_LastOwner_Delete/Update` (`last_owner`) |
| End after start; valid roles; lower-case e-mails; token hash shape | Check constraints |
| One open invitation per e-mail and organisation; unique active room names | Filtered unique indexes |
| One booking per Idempotency-Key, caller and organisation | Filtered unique indexes on `BookingCreationRequests` (signed-in and anonymous scopes) |

`BookingService` catches the trigger error (`SqliteException` code 19 with
`booking_overlap`) and returns the same 409 as its own check.
`BookingConcurrencyTests` proves that direct SQL inserts and updates cannot
bypass the overlap rule.

**Timestamps** are stored as UTC ticks in `INTEGER` columns (a value converter
in `BookingDbContext`), so comparisons in triggers and queries are numeric and
independent of time zones and text formats.

### Transactions and concurrency [Tested]

Each change runs in one transaction started with
`Database.BeginTransactionAsync()`. With Microsoft.Data.Sqlite, a non-deferred
transaction (the default) is issued as `BEGIN IMMEDIATE`, which takes SQLite's
write lock at the start. "Check, then write" sequences (overlap check then
insert; "is this the last Owner?" then delete; idempotency lookup then insert)
therefore cannot interleave with another writer.

Note that this guarantee comes from a **library default**, not from anything
visible in the calling code. Switching database provider, or passing
`deferred: true`, would change it; the database triggers remain the backstop.

### Start-up: migrate safely [Tested]

`DatabaseInitializer.InitialiseAsync` runs before the API accepts requests:

1. If migrations are pending and the database file already has data, it takes
   a verified backup first (`DatabaseBackup.CreateVerifiedAsync`, see
   [section 11](#11-backup-and-restoration)). If the backup fails, nothing is
   migrated and no backup file is left behind.
2. A non-empty file **without** migration history is refused untouched: its
   schema is unknown.
3. After migrating, `VerifyAsync` checks foreign keys, integrity and that all
   four triggers exist. Otherwise the API does not start.

## 8. Core workflows

### 8.1 Registration [Tested]
`POST /api/me/registration` → `UserRegistrationService`. One `BEGIN IMMEDIATE`
transaction looks up `(provider, iss, oid)` and creates `User` + `UserIdentity` if
missing (provider `Entra` or `ExternalId`, from the validating scheme): `201` the first time, `200` afterwards. A unique-index violation from
a concurrent writer is answered with that writer's user. A test sends 12
parallel first registrations and expects exactly one user. Registration grants
no memberships.

### 8.2 Creating an organisation [Tested]
`POST /api/organizations` with an `Idempotency-Key` header →
`OrganizationService`. Repeating the same key and name returns the original
organisation (`200`, `Idempotent-Replayed: true`); the same key with another
name is `422`. The organisation, the Owner membership and two audit events are
written in one transaction. Limit: 10 per user per 24 hours (`429`). A test
sends 8 parallel requests with the same key and expects one `201` and seven
replayed `200`s (`ParallelRequests_WithTheSameKey_CreateOneOrganization`).

### 8.3 Invitations [Tested]
Code: `InvitationService`, [`InvitationToken.cs`](../src/MeetingRoomBooking.Api/Features/Organizations/InvitationToken.cs).

- Creating: 32 random bytes, base64url. The token is returned **once**; the
  database stores only its SHA-256 hash, so a stolen database copy cannot be
  used to accept invitations. Valid for 7 days.
- Accepting requires **both** the token and a verified e-mail equal to the
  invited address: the token proves the link arrived, the e-mail proves who
  opened it. The inviter must still have the right to invite that role.

### 8.4 Bookings [Tested]
`BookingService.CreateAsync` / `UpdateAsync` / `DeleteAsync`:

- Start must be after the server's current time (`TimeProvider`, replaceable
  in tests). Updates that keep the period are allowed for past bookings.
- The room must be active and in the caller's organisation.
- Overlap check excludes the booking being updated; adjacent bookings
  (10:00–11:00 after 09:00–10:00) are allowed.
- Members may only change their own bookings, and see only their own
  bookings' title and "booked by" (`MaySeeDetails`; [ADR 0006](adr/0006-booking-details-hidden-from-members.md)).

**Idempotent creation [Tested]** ([ADR 0005](adr/0005-idempotent-booking-creation.md)).
Both `POST …/bookings` endpoints accept an optional `Idempotency-Key` (a GUID).
Inside the same `BEGIN IMMEDIATE` transaction, `CreateAsync` first
reauthorizes the caller, then (`ReplayAsync`) deletes keys older than 24 hours
and looks the key up for this organisation and user. A known key with the same
request (SHA-256 of room, title, booked-by and the start/end instants as sent)
returns the existing booking with `200` and `Idempotent-Replayed: true`; with
another request `422 idempotency_key_reused`; if that booking was deleted
since, `410 created_booking_deleted`. A new key is stored together with the new
booking, so after a crash both exist or neither does. Without the header
nothing changes. Booking creation writes no audit event, with or without a key.

### 8.5 The first Owner of the legacy organisation [Tested]
`LegacyOwnerBootstrap`: the configuration value
`Organizations:InitialLegacyOwnerUserId` makes a registered user the first
Owner, only while the legacy organisation has no Owner. Later members join by
invitation.

### 8.6 Members and rooms [Tested]
`MembershipService` changes roles and removes members (the last Owner cannot
be demoted or removed: service check plus triggers). `RoomAdministrationService`
creates, edits, archives and restores rooms; rooms are never deleted, and
archiving is refused while a room has upcoming bookings. Both check the
caller's access again inside their write transaction (section 6). All changes
write audit events (`AuditEventType`). There is **no endpoint to read audit events**
yet [Not implemented].

### Error contract
Errors are Problem Details (RFC 9457). Business and access errors are created
in [`ApiProblems.cs`](../src/MeetingRoomBooking.Api/Features/ApiProblems.cs)
and carry a stable `code` extension (for example `booking_overlap`,
`organization_not_found`, `idempotency_key_reused`, `created_booking_deleted`);
clients branch on `code`, while `title` and
`detail` are for people. Input validation errors are standard
`ValidationProblem` responses (400) with an `errors` object per field.

## 9. Testing strategy

| Layer | What | Where |
| --- | --- | --- |
| API integration | The real app via `WebApplicationFactory`, environment `Testing`, a temporary SQLite file per factory, `Pooling=False` | `MeetingRoomBookingApiFactory` |
| Authentication | Tokens signed with a test-only RSA key; production validation code otherwise unchanged | `EntraTestTokens`, `CurrentUserEndpointTests` |
| External ID | A second test key and metadata in the documented external-tenant issuer format: accepted tokens, the provider stored, rejection of the wrong audience, issuer, tenant or key, the e-mail rule (off by default, never set by a token), invitations, start-up validation | `ExternalIdTestTokens`, `ExternalIdAuthenticationTests` |
| Time | `TestTimeProvider` replaces `TimeProvider`, so "must start in the future" tests do not decay | `BookingTimeValidationTests` |
| Concurrency | Parallel requests; direct SQL that tries to bypass triggers | `BookingConcurrencyTests`, `RegistrationEndpointTests` |
| Access revoked mid-request | A real request held after the access filter by a test-only EF Core interceptor (`TransactionGate`) while its caller is disabled, removed or demoted; no timing | `MutationReauthorizationTests` [Tested] |
| Idempotent booking creation | Repeats, concurrent duplicates, a repeat while the first holds the write lock, the answer lost after `COMMIT` (`TransactionGate.AfterCommit`), an API restart on the same file, user and organisation scope, revocation before and during a retry, retention, deleted booking | `BookingIdempotencyTests` [Tested] |
| Migrations | Databases built from older schemas with raw SQL, then migrated; data, IDs and triggers checked | `Migrations/*` |
| Backup/restore | Snapshot round trip, WAL contents, no-overwrite rules, restored DB passes start-up checks | `DatabasePortabilityTests` [Tested] |
| Backup under writes | Repeated backups and restores while another connection commits bookings; a commit exactly between snapshot and copy; a saved but uncommitted booking; clean-up after a failed backup; no overwrite; fingerprints | `DatabaseBackupConcurrencyTests`, `DatabaseBackupTests` [Tested] |
| Restored API | Two-tenant data backed up while the API runs, restored, and served by a second API: same rows, 401 without a valid token, 404 for non-members, roles intact, original unchanged | `RestoredDatabaseApiTests` [Tested] |
| Environment defaults | Production vs Development: Swagger only in Development; HTTP redirected to HTTPS outside Development (and not redirected without a known HTTPS port); an unhandled exception is a generic 500 problem without its details in every environment; CORS headers only in Development | `ProductionDefaultsTests`, `CorsTests` [Tested] |
| Safety | `TestDatabaseGuard` refuses any database outside the temp folder or inside the API project | `TestDatabaseGuard` |
| Booking privacy | Members get others' bookings without title and booked-by; Owners/Admins in full; role change applies to the next request; original endpoints keep their shape | `BookingPrivacyTests` [Tested] |
| Documented journey | The README's demonstration sequence as one test: organisation, rooms, invitations, search, booking, overlap, privacy, roles, last Owner, archiving, isolation, removal | `DocumentedJourneyTests` [Tested] |
| Demo script | `scripts/Start-Demo.ps1 -Reset -StopAfterSeed` with PowerShell 7 on Linux: builds, starts, seeds three bookings, stops | CI step [Tested] |
| Web client | Angular services, sign-in (`AuthService` with a fake MSAL client, settings file, interceptor) and the page in demo and signed-in mode (Vitest, 31 tests), run in CI | `src/meeting-room-booking-web` [Tested] |
| End to end | The mobile repository's Playwright suite starts this API on a temporary database | room-booking-mobile `e2e/` |

The final Phase 4A validation on 2026-10-24 reported **303 passing backend tests**
(`dotnet test`, Release, Windows) at commit `228e1eb`, later merged in PR #13.
Phase 5A added `RolePermissionMatrixTests`: **309 passing** on 2026-10-26
(Release, Windows), on `main` at `c2ca321` plus that change.
Phase 5B added `MutationReauthorizationTests` (33 tests): **342 passing**
on 2026-10-26 (Release, Windows), on `main` at `9fcd827` plus that change.
Against `main` without the service changes, 26 of its 32 request tests fail.
Phase 5C added `ProductionDefaultsTests` (7 tests, no production code
changed): **349 passing** on 2026-10-27 (Release, Windows), on `main` at
`064739a` plus that change.
Phase 5D added `BookingIdempotencyTests` (28 tests) and a CORS test:
**378 passing** on 2026-10-27 (Release, Windows), on `main` at `a5b3f91`
plus that change.
On 2026-10-28, `BookingPrivacyTests` (6, all failing before the change) made
**384** (PR #19), and `DocumentedJourneyTests` **385** (PR #22), in CI and
locally (Release, Windows). The Angular suite went from 8 to **31** tests with
browser sign-in (PR #21).
On 2026-10-29, `ExternalIdAuthenticationTests` (34) made **419** (Release,
Windows), on `main` at `5fd17f1` plus the External ID change.

**Not covered by automated tests:** real Entra and External ID tokens and metadata, a physical
device, a real deployment (TLS certificates, a reverse proxy, hosting
configuration), and performance under load.

## 10. Local development and deployment

- **Run the API:** `dotnet run --project src/MeetingRoomBooking.Api --launch-profile http`
  → `http://localhost:5003`. Set `Entra__ApiClientId` for signed-in endpoints.
- **Database location:** see [below](#which-database-file-the-api-uses).
- **Web client:** `npm --prefix src/meeting-room-booking-web start` →
  `http://localhost:4200`, proxied to the API. Signed out it calls the legacy
  endpoints without a token, so it needs `LegacyApi:AllowAnonymous=true`
  (Development only). With `public/auth-config.json` it offers Microsoft
  sign-in and books in the user's organisations
  ([client README](../src/meeting-room-booking-web/README.md),
  [ADR 0007](adr/0007-browser-sign-in-for-the-angular-client.md)) [Tested]
  with a fake MSAL client; [Manual] a real browser sign-in with a personal
  Microsoft account on 2026-10-29: organisations, booking create, change and
  delete, reload, sign-out and sign-in again
  ([record](testing.md#manual-browser-sign-in-check-2026-10-29)).
- **CI:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) builds and
  tests backend and frontend on every push to `main` and every pull request.
- **Deployment: [Not implemented]** in this repository. There is no
  Dockerfile, infrastructure-as-code or deployment workflow; any hosting is
  configured outside the code.

### Which database file the API uses

There is exactly one input: the connection string `BookingDatabase`, read
once in [`Program.cs`](../src/MeetingRoomBooking.Api/Program.cs)
(`GetConnectionString("BookingDatabase")`, lines 13–17). If it is missing,
the API refuses to start. ASP.NET Core configuration resolves it from the
usual sources, the later winning:

1. `appsettings.json`: `Data Source=meeting-room-booking.db`.
2. `appsettings.{Environment}.json` (does not set it today).
3. Environment variables: `ConnectionStrings__BookingDatabase`
   (the double underscore stands for `:`).
4. Command-line arguments.

A relative `Data Source` is resolved against the **process working
directory**. `dotnet run --project …` starts the API with the project folder
as its working directory (its log shows `Content root path:
…\src\MeetingRoomBooking.Api`), so the default `meeting-room-booking.db` is
`src/MeetingRoomBooking.Api/meeting-room-booking.db` wherever the command is
run from, and a relative path such as `.demo/x.db` points into that folder
too (observed on 2026-10-28: a relative demo path from the repository root
failed with "unable to open database file"). A started build output or
published API uses whatever directory it is started in. Use absolute paths.
`scripts/Start-Local.ps1` [Manual] removes that ambiguity:
it turns `-DatabaseFile` into an absolute path, refuses to start when the file
is missing (unless `-InitializeEmpty`), prints the path, and sets
`ConnectionStrings__BookingDatabase` only for the API process, restoring the
previous value afterwards.

The automatic pre-migration backups go to `Database:BackupDirectory`, or to a
`backups` folder next to the database file when that is not set
([`DatabaseExtensions.cs`](../src/MeetingRoomBooking.Api/Data/DatabaseExtensions.cs),
`DatabaseBackup.CreateVerifiedAsync`). The integration tests set both values
to files in the temp folder, and `TestDatabaseGuard` refuses any database
outside it.

## 11. Backup and restoration

| Capability | Status |
| --- | --- |
| Verified backup before every migration of an existing database | [Tested] |
| Manual backup: `scripts/Backup-Local.ps1` → `RoomBooking.DatabaseTool backup` | [Manual], tested on a development-data copy |
| Restore into a **new** file only: `scripts/Restore-Local.ps1` → `restore` | [Manual], tested on a development-data copy |
| Start the API on a chosen database file: `scripts/Start-Local.ps1` | [Manual], tested on a development-data copy |
| API on a restored file still enforces sign-in and tenant boundaries | [Tested] (`RestoredDatabaseApiTests`) |
| Recovery test with a copy of the real development data | [Manual], 2026-10-24 (see [local-portability.md](local-portability.md#recovery-test-on-a-copy-of-the-real-data-2026-10-24)), including invariant and WAL-only-commit checks on copies ([follow-up](local-portability.md#follow-up-checks-on-the-same-copies-2026-10-24)) |
| Automatic scheduled or off-site backups | [Not implemented] |
| Export/import between different database engines | [Not implemented] |

How restore protects data (`DatabasePortability.RestoreToNewAsync`):

1. Refuses if source equals destination, or if the destination or a leftover
   `-wal`/`-shm` file exists.
2. In one read transaction on the backup, verifies it (`integrity_check`,
   `foreign_key_check`) and fingerprints it (see below).
3. Copies with SQLite's backup API into a temporary file next to the
   destination, and switches it to WAL mode like the API's own databases.
4. Verifies the copy again (integrity, foreign keys, identical fingerprints).
5. `File.Move` without overwrite: atomic on the same volume, and it fails
   rather than replace a file that appeared in the meantime.

**How a backup is made** (`DatabasePortability.BackupAsync` and the API's
start-up both call `DatabaseBackup.CreateVerifiedAsync`) [Tested]:

1. The source is opened **read-only**, and a deferred read transaction
   (`BeginTransaction(deferred: true)`) is started on it. SQLite fixes that
   transaction's snapshot at its first read and keeps it until it ends, even
   while the API commits more bookings (WAL mode).
2. Inside that transaction, `FingerprintAsync` records each table's row count
   and an order-independent hash of its rows (the sum of truncated SHA-256
   row hashes), plus the same for the schema (`sqlite_master`, so indexes and
   triggers are compared too) and `sqlite_sequence` (the AUTOINCREMENT
   counters).
3. Still inside it, `SqliteConnection.BackupDatabase` (SQLite's online backup
   API) copies **that same snapshot** into `<backup>.partial-<id>.tmp`, which
   is then switched to a rollback journal so that the backup is one
   self-contained file.
4. The copy must pass `PRAGMA integrity_check` and `PRAGMA foreign_key_check`
   and have exactly the fingerprints from step 2.
5. Only then does `File.Move` (no overwrite) give it its final name
   `<database>.manual-<UTC timestamp>.db` (or `.pre-<migration>-…`). On any
   failure, `finally` deletes the temporary file and its own sidecars, nothing
   else.

Because the copy and the reference values come from one snapshot, a booking
committed during the backup can neither be half-included nor make
verification fail. Commits that still sit in the `-wal` file are included,
which a plain file copy of a running database would miss.

Earlier versions used `VACUUM INTO`, which also writes a consistent snapshot,
but SQLite refuses to run it inside a transaction ("cannot VACUUM from within
a transaction"). Its snapshot and the row counts it was compared with were
therefore taken at different moments, and a booking committed in between
produced a false "does not have the same rows" failure. The reasoning is in
[ADR 0003](adr/0003-portable-sqlite-backup-and-restore.md).

**Why restore never overwrites.** A restore is only ever needed when something
has gone wrong, which is also when a mistake is most likely: the wrong file
chosen, an older backup picked, a path typed into the wrong field. If restore
could replace a database, one such slip would destroy the only good copy, and
the damage would be invisible until someone missed a booking. Writing to a new
file makes every restore reversible: the original stays where it was, the
restored copy can be inspected and started on its own, and "switching over"
is a separate, deliberate step (`Start-Local.ps1 -DatabaseFile …`). Checking
for leftover `-wal`/`-shm` files matters too: SQLite would apply an old
database's WAL to a new file of the same name and corrupt it.

Step-by-step instructions: [local-portability.md](local-portability.md).
Rationale: [ADR 0003](adr/0003-portable-sqlite-backup-and-restore.md).

## 12. Design decisions and trade-offs

| Decision | Why | Cost | Record |
| --- | --- | --- | --- |
| Tenant isolation in one route-group filter plus scoped queries | Cannot be forgotten per endpoint; 404 hides other tenants | Every organisation request makes a membership query | [ADR 0001](adr/0001-organisation-scoped-access-control.md) |
| Critical invariants in SQLite triggers as well as services | Holds under races, bugs and manual SQL | Logic in two places; SQLite-specific SQL in migrations | [ADR 0002](adr/0002-database-enforced-invariants.md) |
| SQLite file + verified snapshots, restore to new file only | Runs anywhere without a server; cheap, portable backups | One writer at a time; one API instance; no automatic sync | [ADR 0003](adr/0003-portable-sqlite-backup-and-restore.md) |
| Feature folders | A feature is understood by opening one folder | Shared concerns (`ApiProblems`, `Data`) sit beside features | – |
| Result enums from services, HTTP mapping in endpoints | Services testable and HTTP-agnostic | Some mapping code per feature | – |
| Changes reauthorize under the write lock; a lost access is an exception the filter maps | Check and write see the same membership; one mapping for 403/404 | Services depend on the filter to translate it; two extra reads per change | [ADR 0004](adr/0004-reauthorize-mutations-under-the-write-lock.md) |
| Booking Idempotency-Keys in their own table, written in the booking's transaction | Survives restarts and crashes; a deleted booking keeps its key used; retention without touching bookings | Three extra statements per keyed creation under the write lock | [ADR 0005](adr/0005-idempotent-booking-creation.md) |
| `TimeProvider` injected | Time-dependent rules testable at exact instants | – | – |
| Identity = issuer + `oid`, never e-mail | E-mail is mutable and not unique across account types | Linking two accounts of one person is not supported yet | – |

## 13. Known limitations

- **Single writer, single instance.** SQLite serialises writes, and the
  database is a local file, so the API must run as one instance with
  persistent disk.
- **Sign-in needs the internet.** Entra is an external identity provider;
  fully offline sign-in is not possible with this design.
- **The Angular client's sign-in has been run against Entra once, by hand,
  with a personal account** (2026-10-29,
  [record](testing.md#manual-browser-sign-in-check-2026-10-29)); a work or
  school account, a first-time user and token expiry are untried there. It
  uses its own SPA registration, separate from the mobile app's. It books
  only; organisation administration is in the mobile app. Its production
  build has no proxy and would need the API on the same origin or CORS that
  allows `Authorization` ([ADR 0007](adr/0007-browser-sign-in-for-the-angular-client.md)).
- **No deployment or production configuration in the repository.** Before
  a deployment, at least:
  - Listen on HTTPS only, or make sure the HTTPS port is known (Kestrel
    HTTPS endpoint or `HTTPS_PORT`). `UseHttpsRedirection` redirects nothing
    without it (pinned by `ProductionDefaultsTests`). A redirect only helps
    browsers anyway: a client that sent a bearer token over plain HTTP has
    already exposed it. The mobile app never sends one over HTTP in a release
    build (`canSendBearerToken` in the mobile repository).
  - Behind a reverse proxy that ends TLS, configure forwarded headers
    (`UseForwardedHeaders` with the proxy as a known proxy), so the API sees
    the original scheme.
  - Restrict `AllowedHosts` (it is `*`) to the API's host name.
  - HSTS (`UseHsts`) is not configured; it only affects browsers, which do
    not call the API with tokens today.
- **Restore does not check versions.** A backup made by a newer API version
  can be restored and started by an older one; nothing warns about it.
- **A killed backup can leave a `*.partial-<id>.tmp` file.** It never has a
  backup's name and is safe to delete. Backups are not compacted (the backup
  API copies pages as they are, unlike `VACUUM INTO`).
- **The backup fingerprint is not tamper-proof.** It detects an incomplete or
  accidentally different copy, not a deliberately crafted one.
- **One `Start-Local.ps1` per checkout.** It builds Debug in place, and a
  running instance locks the output, so a second instance fails at the build.
- **No account linking**, no audit-log endpoint, no pagination, no general
  rate limiting (only organisation creation is limited).
- **E-mail one-time-code sign-in (Entra External ID) has not met Microsoft
  yet.** The API side is implemented and tested with test tokens; no external
  tenant exists, so the issuer format, the claims and a real sign-in are
  unverified ([open checks](external-id-setup.md#verification-against-microsoft)).
  E-mail users cannot accept invitations until `ExternalId:EmailClaimIsVerified`
  is checked and turned on. Room Booking stores no passwords either way.
- **Transaction mode is implicit** (see [section 7](#transactions-and-concurrency-tested)).
- **Booking Idempotency-Keys last 24 hours.** A retry after that is a new
  request (the overlap rule still refuses a second booking of the same slot).
  Expired keys are deleted by the next *successful* keyed creation; a refused
  request rolls its clean-up back. In anonymous demo mode all callers share
  one key scope.
- **Reads use the access read at the start of the request.** Changes check
  again under the write lock (Phase 5B); a `GET` running while its caller is
  removed still returns what they could see a moment earlier (see
  [role-permission-matrix.md](role-permission-matrix.md#known-limitation)).

## 14. How this project was built

This project was developed with AI assistance (Claude Code). Some commits
carry a `Co-Authored-By: Claude` trailer; those trailers are not a complete
record of AI involvement.

Every change is verified the same way regardless of who wrote it: the
automated test suite and CI must pass, database changes are tested on copies
of real data, never the original, and behaviour on a physical device is
checked by hand. The [Status labels](#room-booking--developer-walkthrough-api-and-web)
in this document record which of those checks applies to each part.
