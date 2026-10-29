# ADR 0008: Microsoft Entra External ID as a second token issuer

- **Status:** Accepted. Implemented, covered by integration tests with
  test-signed tokens, and run against a real external tenant on 29 October
  2026 ([setup and results](../external-id-setup.md#verification-against-microsoft)).
  Revised the same day: no verified e-mail for External ID users at all.
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
- **No verified e-mail for External ID users, and no switch for one.**
  `xms_edov` does not fit external tenants, and Microsoft documents the
  `email` claim as not guaranteed to be correct and mutable, so a claim alone
  is never enough. E-mail users therefore cannot accept invitations
  (`verified_email_required`). (Revised 29 October 2026: the earlier
  `ExternalId:EmailClaimIsVerified` switch, which trusted the claim after a
  manual check, was removed before it was ever turned on.)
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
- **Trusting `email` from External ID, at once or behind a switch:** it would
  rest on a claim Microsoft calls unreliable. A claim in a token is not a
  guarantee, however often it happens to be right.
- **Reading the user's e-mail identity from Microsoft Graph on their behalf**
  (on-behalf-of, delegated `User.Read`): implemented and tried against the
  real tenant. The directory records the code's address as an identity with
  issuer `mail`, but `GET /me?$select=identities` returned an empty list
  under `User.Read`, the only delegated Graph permission external tenants
  allow besides sign-in. Withdrawn.
- **App-only Graph lookup (client credentials, `User.Read.All`):** would read
  that identity, but client credentials in external tenants are the
  separately billed M2M Authentication add-on; a paid dependency does not
  fit a project that must run locally without paid services.
- **A custom claims provider (`TokenIssuanceStart`):** puts the address into
  the token from Microsoft's side, but needs a hosted HTTPS endpoint, which a
  locally run project does not have.
- **Auth0 or another e-mail provider:** rejected in phase 0 (21 October); it
  would add a third party for what the agreed Microsoft platform provides.

## Consequences

- E-mail users can sign in, register, create organisations and book, but
  cannot accept invitations. To join someone else's organisation they use a
  Microsoft account, signed in directly or linked to the e-mail user.
- A person with a Microsoft account and an e-mail sign-in is two users until
  the identities are linked explicitly ([ADR 0009](0009-explicit-identity-linking.md);
  never by e-mail).
- Custom URL domains for the external tenant are not supported: the selector
  recognises `ciamlogin.com` issuers only.
- Microsoft's sign-in page treats an address that is also a personal
  Microsoft account as such, and offers it no working sign-up link there
  (the sign-up page works); see the setup document.
