# ADR 0005: Idempotent booking creation with a persisted Idempotency-Key

- **Status:** Accepted (Phase 5D; PR #18 merged)
- **Code:** `Features/Bookings/BookingCreationRequest.cs`
  (`BookingCreationRequest`, `IdempotentBookingRequest`),
  `Features/Bookings/BookingService.cs` (`CreateAsync`, `ReplayAsync`),
  `Features/Bookings/BookingEndpoints.cs`, migration
  `20261027084641_AddBookingCreationRequests`

## Context

A phone can create a booking and then lose the answer (timeout, dropped
connection, app in the background). The user cannot tell whether the booking
exists. Repeating the request today returns `409 booking_overlap` for the
booking the user just made, which reads as "someone else took the room".
Creating organisations already solves this with an `Idempotency-Key` header
stored on the organisation row (`200` + `Idempotent-Replayed: true` for a
repeat, `422 idempotency_key_reused` for the same key with another name).

## Decision

- `POST /api/organizations/{id}/bookings` and the legacy `POST /api/bookings`
  accept an optional `Idempotency-Key` (a non-empty GUID; anything else is a
  `400` validation problem on `Idempotency-Key`). Without the header nothing
  changes.
- A successful keyed creation stores a row in **`BookingCreationRequests`**
  (organisation, caller's user ID, key, SHA-256 of the request, booking ID,
  time) **in the same `BEGIN IMMEDIATE` transaction as the booking**. After a
  crash both exist or neither does.
- Order under the write lock: (1) `ReauthorizeAsync` (ADR 0004), so a caller
  who lost access gets `403/404` and never the stored booking; (2) delete
  keys older than 24 hours, then look up the key **scoped to organisation +
  user** (the operation is the table itself); (3) only for a new key the
  usual checks (start in the future, active room, overlap) and the insert.
- Same key, same request → `200` with the booking as it is now, plus
  `Idempotent-Replayed: true`. The hash covers the request as sent (room,
  title, booked-by, start and end as instants), before the API fills in
  "booked by" from the caller's name.
- Same key, other request → `422 idempotency_key_reused`. Key whose booking
  has since been deleted → `410 created_booking_deleted` (the foreign key is
  `ON DELETE SET NULL`, so the key stays used).
- Refused requests store nothing, so repeating them simply runs them again.
- Anonymous demo mode (Development/Testing only) has no user: its keys share
  one scope per organisation, which only exposes legacy bookings that
  anonymous callers can already list.
- Development CORS allows the `Idempotency-Key` request header.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Key columns on `Bookings` (as on `Organizations`) | Deleting the booking would forget the key (a repeat creates it again); no clean retention without touching bookings; anonymous bookings have no creator to scope by. |
| In-memory cache (`IMemoryCache`) | Lost on restart and not atomic with the insert: a crash between commit and caching re-creates the booking. |
| Store the full response body and replay it verbatim | Would hand a stale or no-longer-permitted representation to the caller; the current booking under the current role is more truthful and cheap to read. |
| Store failures too (Stripe-style) | Nothing is committed for a refusal, so re-running it is safe and gives a current answer. |
| A `409 in progress` state for concurrent duplicates | `BEGIN IMMEDIATE` already serialises them; the second simply waits and replays. |
| Background clean-up job | More moving parts; the table grows only with keyed bookings, and the indexed delete inside the write transaction is small. |

## Consequences

- Positive: a lost answer can be retried safely, across API restarts, with
  no duplicates and no information leak across users or organisations.
- Positive: the overlap triggers stay the final word; even after a key
  expires, repeating the same request hits `409 booking_overlap`.
- Negative: two extra statements (clean-up delete, lookup) plus one insert
  per keyed creation, inside the write lock.
- Negative: the clean-up is part of the request's transaction, so a refused
  request rolls it back; expired rows are removed by the next *successful*
  keyed creation (an expired key is never answered either way).
- Negative: a key is remembered for 24 hours only; a retry after that is a
  new request.
- Neutral: the legacy and organisation endpoints share the key scope for the
  legacy organisation, since both create the same thing for the same caller.

## Tests

`BookingIdempotencyTests` (28 cases): first and repeated creation, other
offset for the same instant, retry after the start passed, same key with other
details, 8 concurrent identical requests, a repeat sent while the first holds
the write lock (`TransactionGate`), API restart on the same file, the response
lost after `COMMIT` (new `TransactionGate` point `AfterCommit`) and before it,
organisation and user isolation, revocation before the retry and while the
retry waits for the lock, role change, overlap with another key, requests
without a key, the anonymous legacy endpoint, legacy and organisation endpoint
sharing a key, invalid keys, no audit events, deleted booking, retention.
Moving the lookup before `ReauthorizeAsync` makes the two "revoked while
waiting for the lock" tests fail.
