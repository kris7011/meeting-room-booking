# ADR 0008: Microsoft Entra External ID as a second token issuer

- **Status:** Accepted. Implemented and covered by integration tests with
  test-signed tokens; **not yet run against a real external tenant**
  ([setup and open checks](../external-id-setup.md)).
- **Code:** `Features/Authentication/ExternalIdAuthentication.cs`,
  `ExternalIdAuthenticationOptions.cs`, `EntraAuthentication.cs` (policy
  scheme), `Features/Users/SignedInIdentity.cs`

## Context

The plan agreed on 21 October 2026 has two ways to sign in: Microsoft 365 and
personal Microsoft accounts through Entra ID multi-tenant (done), and **e-mail
with a one-time code through Microsoft Entra External ID**. External ID runs
in a separate *external tenant* with its own app registrations, so its access
tokens have another issuer (`https://{tenant ID}.ciamlogin.com/{tenant ID}/v2.0`),
other signing keys and another audience. `UserIdentities.Provider` already
allowed `ExternalId`.

## Decision

- **A second JWT bearer scheme, `ExternalId`,** with the external tenant's
  metadata as authority. Its issuer is the one that metadata names, not a
  string built by hand, and `tid` must equal the configured tenant. This
  follows Microsoft's guidance for an API that accepts tokens from two
  issuers: one scheme per issuer, each with its own keys, issuer and audience.
- **A policy scheme `SignedIn` as the default.** It reads the unvalidated
  `iss` and forwards to `ExternalId` for `https://….ciamlogin.com` issuers,
  otherwise to Entra's `Bearer`. The selected scheme validates everything.
- **One authorisation policy and one scope name** (`access_as_user`) for both
  issuers, so every existing endpoint accepts both without change.
- **Provider from the validating scheme.** `SignedInIdentity.Provider` comes
  from the authentication type the scheme gives its identity, never from a
  claim. The key stays issuer plus `oid`.
- **No verified e-mail from External ID by default.** `xms_edov` does not fit
  external tenants, and Microsoft does not document that the access token's
  `email` claim is the address the code went to. `ExternalId:EmailClaimIsVerified`
  opts in after a real token has been checked; the API then adds its own
  marker identity, which a token cannot imitate.
- **External ID requires the Entra settings.** It is an additional way to
  sign in; the API refuses to start with it alone.
- **Browser-delegated sign-in, not native authentication.** The app keeps the
  system browser and authorization code with PKCE, as for Entra ID. Native
  authentication (in-app UI) would need another SDK and supports local
  accounts only.

## Alternatives considered

- **One scheme accepting both issuers** (several valid issuers and key sets in
  one set of parameters): fewer lines, but a key or audience of one issuer
  could validate a token of the other, and Microsoft advises against it.
- **Two schemes listed in the policy, no selector:** every token is validated
  twice and one of the attempts always fails and logs noise.
- **Microsoft.Identity.Web:** the API already validates with plain JWT bearer
  and `Microsoft.IdentityModel.Validators`; a second library for one more
  scheme would add a dependency for little.
- **Trusting `email` from External ID right away:** it would let e-mail users
  accept invitations immediately, but on an assumption about Microsoft's
  tokens that no document states. Refusing until it is checked is the safe
  default.
- **Auth0 or another e-mail provider:** rejected in phase 0 (21 October); it
  would add a third party for what the agreed Microsoft platform provides.

## Consequences

- E-mail users can sign in, register, create organisations and book. Until
  `EmailClaimIsVerified` is on, they cannot accept invitations.
- A person with a Microsoft account and an e-mail sign-in is two users until
  the identities are linked explicitly (a later change; never by e-mail).
- Custom URL domains for the external tenant are not supported: the selector
  recognises `ciamlogin.com` issuers only.
- The issuer format, the claims and the sign-in itself are unverified until
  the external tenant exists.
