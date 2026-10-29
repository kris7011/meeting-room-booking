# API reference

Endpoints, business rules and error codes of the Room Booking API. The
[README](../README.md) gives the overview, the
[walkthrough](PROJECT_WALKTHROUGH.md) explains the code behind these rules, and
[role-permission-matrix.md](role-permission-matrix.md) lists who may call what,
with the test that proves each cell. Swagger: `http://localhost:5003/swagger`
in Development.

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

## Organisation access control

Every organisation's rooms and bookings are reached through `/api/organizations/{organizationId}/…`, where `{organizationId}` is the organisation's public ID (a GUID). The ID alone never grants anything. Before any of these endpoints runs, one filter (`OrganizationAccessFilter`) checks, in this order:

| Check | Failure |
| --- | --- |
| A valid access token (see [What a token must satisfy](entra-setup.md#what-a-token-must-satisfy)) | `401` |
| The identity is registered | `403 user_not_registered` |
| The user is not disabled | `403 user_disabled` |
| The user is a member of that organisation, and it is not deleted | `404 organization_not_found`: an organisation you are not in looks exactly like one that does not exist |
| The endpoint's role requirement, if it has one | `403 organization_permission_denied` |

Every endpoint, the roles it admits and the tests that prove it are listed in [docs/role-permission-matrix.md](role-permission-matrix.md).

The membership and role are read from the database for every request; nothing in the request body or the token says which organisations someone belongs to. Every query below the filter is limited to the caller's organisation, so a room or booking ID from another organisation is `404 meeting_room_not_found` / `404 booking_not_found`, whether or not it exists. Integer IDs can be guessed, but guessing one never reaches another organisation's data.

| Role | Rooms and bookings |
| --- | --- |
| Owner | Search, book, see, change and delete every booking of the organisation |
| Admin | As Owner |
| Member | Search and book; change and delete only bookings they made (`403 booking_permission_denied` otherwise). Other people's bookings show their room and time, but not their title or who booked them |

Booking details are private: a Member gets another person's booking with `title` and `bookedBy` set to `null`, and the organisation endpoints add `detailsHidden: true`. Everyone can still see which room is taken when. Owners and Admins see every booking in full, and the anonymous demo mode shows everything ([ADR 0006](adr/0006-booking-details-hidden-from-members.md)).

A new booking records the signed-in user as its creator (`CreatedByUserId`) and every change the user who made it (`UpdatedByUserId`, `UpdatedUtc`). `bookedBy` defaults to the user's display name when it is left out. Bookings made before sign-in have no creator, so only Owners and Admins can change them. The organisation responses add `createdByCurrentUser`, `canModify` and `detailsHidden`, so a client can show or hide its edit and delete actions; the API checks every change itself regardless.

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

or `$env:LegacyApi__AllowAnonymous = "true"`. This is only accepted in the `Development` and `Testing` environments: in any other environment the API refuses to start with it, so real organisation data is never anonymous in production. In demo mode a token sent to these endpoints is ignored, and the organisation endpoints still require sign-in. The default, in `appsettings.json`, is `false`. The Angular client calls the original endpoints without a token while signed out, so it needs demo mode then; signed in, it uses the organisation endpoints ([client README](../src/meeting-room-booking-web/README.md)).

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

For Microsoft accounts (Entra ID), the API treats the `email` claim as verified only when the token also carries `xms_edov: true`. Microsoft documents `email` on its own as unverified and mutable, so without `xms_edov` the identity has no verified e-mail. `preferred_username` is never used as an e-mail. A verified e-mail is only ever compared with the address of an invitation, together with the invitation's secret token; it never grants access on its own.

To get these claims, add the **optional claims** `email` and `xms_edov` to the **access token** of the API's app registration (step 4 under the admin-center steps above). Without them, registration still works, but invitations cannot be accepted (`verified_email_required`).

E-mail one-time-code sign-ins (External ID) have no verified e-mail unless `ExternalId:EmailClaimIsVerified` is turned on, after checking a real token ([external-id-setup.md](external-id-setup.md#e-mail-addresses)). `GET /api/me` says which issuer signed the caller in: `"provider": "Entra"` or `"provider": "ExternalId"`.

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

## Date and time handling

The browser form uses `datetime-local`, which represents a local date and time without timezone information.

Before a booking request is sent, Angular converts the local value into an ISO UTC timestamp using `toISOString()`.

The API normalises and stores booking timestamps as UTC values.

When an existing booking is edited, its UTC timestamp is converted back into the local format expected by the `datetime-local` input.

For a larger production system, the organisation or meeting room timezone should also be modelled explicitly.

## CORS

The Angular client avoids CORS through its development proxy. Browser clients served from another origin call the API directly, so the API must allow that origin.

In the Development environment, the API allows the origins listed in `appsettings.Development.json`:

```json
"Cors": {
  "AllowedOrigins": [
    "http://localhost:8081"
  ]
}
```

`http://localhost:8081` is the Expo dev server of the [room-booking-mobile](https://github.com/kris7011/room-booking-mobile) app running in a browser. Only the methods `GET`, `POST`, `PUT` and `DELETE` and the headers `Accept`, `Content-Type` and `Idempotency-Key` are allowed (`Program.cs`); `Authorization` is not.

Outside Development no CORS policy is registered, even if `Cors:AllowedOrigins` is set, so the API sends no CORS headers and browsers keep blocking cross-origin requests. Native mobile apps are not affected by CORS.
