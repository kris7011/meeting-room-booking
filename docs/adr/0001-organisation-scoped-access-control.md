# ADR 0001: Organisation-scoped access control in one endpoint filter

- **Status:** Accepted (recorded retroactively, 2026-10-24)
- **Code:** `Features/Organizations/OrganizationAccess.cs`, `Features/Organizations/LegacyApiAccess.cs`

## Context

Several organisations share one API and one database. A member of one
organisation must never read or change another organisation's rooms,
bookings, members or invitations. Requirements:

- Identity comes from a validated Entra access token, never from the client.
- Membership and role can change at any time and must take effect at once.
- Adding a new endpoint must not require remembering a security check.
- An outsider should not learn which organisations exist.

## Decision

1. All organisation endpoints are mapped in one route group,
   `/api/organizations/{organizationId:guid}` (`MapOrganizationGroup`), which
   requires the `SignedInUserPolicy` and runs `OrganizationAccessFilter`
   before every endpoint.
2. The filter loads the registered user by `(iss, oid)` and their membership
   of the route's organisation **from the database on every request**, checks
   the endpoint's role metadata, and publishes the result as the scoped
   `CurrentOrganization`.
3. A non-member gets **404** `organization_not_found`, the same as a
   non-existent organisation.
4. Every query below the filter is additionally limited to the caller's
   `OrganizationId` (for example `BookingService.BookingsOf`).
5. Per-object rules (a Member may change only their own booking) stay in the
   services.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Roles and organisation IDs as token claims (Entra app roles or groups) | Changes would apply only after the token expires; ties the authorization model to Entra configuration; one user in many organisations is awkward in app roles. |
| A check at the top of each endpoint | Easy to forget in a new endpoint; duplicated code. |
| ASP.NET Core policy-based authorization with a resource handler | Viable and more conventional. The filter was simpler for "resolve membership once and hand it to the endpoint", which policies do not do on their own. |
| EF Core global query filters per tenant | Hides the filtering, which makes queries harder to reason about, and the legacy endpoints need a fixed organisation. |
| One database per organisation | Strong isolation, but much more operational work than this project needs. |

## Consequences

- Positive: one place enforces tenant access; new endpoints in the group are
  protected by default; role changes apply on the next request.
- Positive: defence in depth: even with a bug in the filter, scoped queries
  return "not found" for other organisations' IDs.
- Negative: one membership query per request (cheap with the composite key,
  but not free).
- Negative: an endpoint mapped *outside* the group is unprotected.
  `CurrentOrganization.Required()` throws if an endpoint expects a caller that
  the filter never set, which catches the mistake in tests.

## When to revisit

If the system needs many more organisations or very high request rates,
cache memberships briefly or move to claims-based authorization with short
token lifetimes. If regulatory isolation is required, consider a database per
tenant.
