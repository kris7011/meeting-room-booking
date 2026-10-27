# ADR 0004: Reauthorize every organisation change under the write lock

- **Status:** Accepted (Phase 5B, mobile issue #20; backend PR #16 merged)
- **Code:** `Features/Organizations/OrganizationAccess.cs`
  (`OrganizationAccessService.ReauthorizeAsync`, `OrganizationAccessDeniedException`,
  `OrganizationAccessFilter`), `Features/Rooms/RoomAdministrationService.cs`,
  `Features/Bookings/BookingService.cs`, `Features/Organizations/OrganizationService.cs`,
  `InvitationService.cs`, `MembershipService.cs`

## Context

`OrganizationAccessFilter` ([ADR 0001](0001-organisation-scoped-access-control.md))
reads the caller's user, membership and role when a request starts and hands
the endpoint an `OrganizationCaller` snapshot. Room administration, booking
changes, renaming and revoking invitations then trusted that snapshot. A
request that had passed the filter when another request disabled, removed or
demoted its caller could still write: for example a removed member's booking,
or a demoted Admin's new room. Only `MembershipService` read the role again
(but not the user's disabled state).

## Decision

- Every organisation-scoped change calls
  `OrganizationAccessService.ReauthorizeAsync(caller, requirement)` **as the
  first statement inside its transaction**, i.e. after `BEGIN IMMEDIATE` has
  taken SQLite's write lock ([ADR 0002](0002-database-enforced-invariants.md))
  and before any lookup or write. It reads the user (enabled?), the membership
  of a non-deleted organisation and the role again, by internal IDs, never
  from token claims. It throws if called outside a transaction.
- Under the write lock no other request can commit a membership change until
  this transaction ends, so the check and the write see the same state.
- The services use the **current** role it returns for their per-object rules
  (`BookingActor.MayModify`, `InvitationService.MayInvite`, "only Owners remove
  others").
- When access is gone it throws `OrganizationAccessDeniedException`. The
  transaction is rolled back by `await using`, and `OrganizationAccessFilter`
  turns the exception into exactly the problem it would have returned had
  the change happened before the request: `403 user_disabled`,
  `404 organization_not_found` (indistinguishable from an unknown
  organisation), `403 organization_permission_denied`. A demoted Admin
  changing someone else's booking gets the existing `403 booking_permission_denied`.
- Anonymous demo mode (legacy endpoints, Development/Testing only) has no
  caller, so there is nothing to recheck; it is unchanged.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Return the access failure in each service's result type | Four result types and the booking delete signature change, and each endpoint maps the same three failures again; easy to forget one. The exception is mapped in the one place that already owns these answers. |
| Take the write lock in the filter for every mutating request | Holds the lock across model binding and validation, and a filter cannot know whether the endpoint writes. |
| Reauthorize before `BEGIN IMMEDIATE` | Leaves the same window, only shorter. |
| Optimistic concurrency token on memberships | Detects changes to rows the request writes; the request reads the membership, it does not write it. |

## Consequences

- Positive: the documented one-request window is closed for all
  organisation-scoped changes, with no schema change or migration.
- Positive: one implementation; a new change only has to call it inside its
  transaction.
- Negative: two indexed point reads per change, inside the write lock. Small
  compared with the change itself.
- Negative: a service relies on the filter to translate the exception. A
  service called outside the organisation group would surface it as a 500;
  every caller today is inside the group (the route-table test in
  `RolePermissionMatrixTests` lists them).
- Reads (`GET`) still use the snapshot: a list fetched just before a removal
  shows what the member could see a moment earlier, and writes nothing.

## Tests

`MutationReauthorizationTests` holds a real request after the filter and
before `BEGIN IMMEDIATE` with a test-only EF Core interceptor
(`TransactionGate`), revokes access, then lets it continue. No sleeps or
timing. Against the code before this change, 26 of its 32 request tests
failed; with it, all pass.
