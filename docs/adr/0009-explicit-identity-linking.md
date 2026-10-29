# ADR 0009: Explicit identity linking with proof of both identities

- **Status:** Accepted. Implemented and covered by integration tests with
  test-signed tokens; not yet run with real Microsoft sign-ins.
- **Code:** `Features/Users/IdentityLinkRequest.cs`, `IdentityLinkService.cs`,
  `IdentityLinkEndpoints.cs`; migration `AddIdentityLinkRequests`

## Context

A person can sign in in more than one way: a work account, a personal
Microsoft account, an e-mail one-time code (External ID). Each identity
becomes its own user (key: issuer + `oid`). The plan of 21 October 2026
requires that identities can be joined to one user, but only with explicit
confirmation of both identities, and never automatically because e-mail
addresses match: an e-mail claim can be unverified or reused, and matching
on it would let whoever controls an address take over an account.

## Decision

Three steps, each a separate request with its own token:

1. **Start** (`POST /api/me/identity-links`), signed in as the existing user.
   The API returns a one-time code (32 random bytes; only its SHA-256 hash is
   stored) valid for 10 minutes. Starting again cancels the previous code.
2. **Claim** (`POST /api/identity-links/claim`), signed in with the identity to
   add, sending the code. This proves that whoever holds the existing session
   also controls the new identity. The API records the identity but links
   nothing. The identity must not be a user of its own.
3. **Confirm** (`POST /api/me/identity-links/{id}/confirm`), signed in as the
   existing user again, after the app has shown both identities. Only now is
   the `UserIdentities` row created, and an `IdentityLinked` audit event
   written.

Every step runs in one `BEGIN IMMEDIATE` transaction. A claimed code is
useless to any other identity, a repeat by the same identity returns the
same answer, and the unique index on `(Issuer, Subject)` stops a confirmation
that races a registration of the same identity.

**No merging.** An identity that is already a user (with memberships or
bookings) cannot be linked; it answers `identity_belongs_to_another_user`.
Moving data between users is a different, riskier operation.

## Alternatives considered

- **Link by verified e-mail:** rejected by the plan and unsafe (see Context).
- **Two steps (start with A, finish with B):** proves both identities, but the
  final, explicit "yes" would come from the new identity, which has not seen
  which account it joins. Confirming with the existing session puts the
  decision with the account that gains a way in.
- **Both tokens in one request:** the client would hold two access tokens for
  one call, and there would be no point between the two proofs to show the
  person what will happen.
- **Merging users:** out of scope; needs rules for conflicting memberships,
  roles and bookings.

## Consequences

- One person can sign in with each linked identity and reaches the same user.
- There is no unlinking yet, so a linked identity stays until an operator
  removes it in the database.
- A wrong confirmation can only add an identity whose control was proven in
  the same 10 minutes by the person holding the account's session.
