# ADR 0002: Critical invariants enforced by the database as well as the services

- **Status:** Accepted (recorded retroactively, 2026-10-24)
- **Code:** `Data/Migrations/20261018141000_PreventBookingOverlap.cs`,
  `Data/Migrations/20261021074224_AddIdentityAndMembershipTables.cs`,
  `Data/BookingDbContext.cs`, `Features/Bookings/BookingService.cs`,
  `Data/DatabaseInitializer.cs`

## Context

Two rules must never be broken, even under concurrent requests:

- A meeting room cannot have two overlapping bookings.
- An organisation cannot lose its last Owner.

A check in C# followed by a write is a race: two requests can both pass the
check before either writes. Bugs, future code paths and manual SQL could also
bypass a service-only check.

## Decision

- The **services check first**, so users get specific errors
  (`409 booking_overlap`, `409 last_owner`).
- Each change runs in one transaction that Microsoft.Data.Sqlite starts as
  `BEGIN IMMEDIATE`, taking the write lock before the check.
- **SQLite triggers** enforce the same rules on every insert and update
  (`TR_Bookings_Overlap_*`, `TR_Memberships_LastOwner_*`). The services map
  the trigger error back to the same response.
- A composite foreign key `(OrganizationId, RoomId)` guarantees that a booking
  belongs to its room's organisation; check constraints guard value shapes.
- At start-up, `DatabaseInitializer.VerifyAsync` refuses to run if any of the
  four triggers is missing, because a table rebuild in a migration silently
  drops triggers.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Service checks only | Race conditions; bypassable by other code or manual SQL. |
| Application-level locks (`SemaphoreSlim`) | Only works within one process. |
| Optimistic concurrency (row versions) | Detects conflicting updates to the *same* row; an overlap is a conflict between *different* rows. |
| Exclusion constraints | Expresses overlap directly, but is a PostgreSQL feature; SQLite has none. |

## Consequences

- Positive: the invariants hold regardless of which code path writes.
  `BookingConcurrencyTests` proves that direct SQL cannot bypass the overlap rule.
- Negative: the rule exists twice (C# and SQL) and must be kept consistent.
- Negative: the triggers are SQLite-specific SQL; moving to another database
  engine means rewriting them (for example as a PostgreSQL exclusion
  constraint).
- Negative: correctness of the check-then-write sequence relies on the
  library's default `BEGIN IMMEDIATE`, which is not visible in the calling code.

## When to revisit

When moving to PostgreSQL or SQL Server: replace the triggers with the
engine's native mechanism and make the transaction isolation explicit.
