# Microsoft Entra External ID: e-mail one-time-code sign-in

How the API accepts sign-ins with an e-mail address and a one-time passcode
through Microsoft Entra External ID, how to configure it, and the manual
steps to create the external tenant. Microsoft account sign-in (Entra ID) is
unchanged and described in [entra-setup.md](entra-setup.md); the decision is
recorded in [ADR 0008](adr/0008-external-id-as-a-second-token-issuer.md).

**Status.** Implemented, covered by integration tests with test-signed
tokens, and **run against a real external tenant** on 29 October 2026
(`roombookingdev`, created for this project): e-mail sign-up and sign-in on
the Samsung development build, registration, a new organisation, explicit
identity linking and Microsoft sign-in next to it. Results and what stays
open: [Verification against Microsoft](#verification-against-microsoft).

## How it works

People who have no Microsoft account, or do not want to use it, sign in on a
Microsoft-hosted page of an **external tenant**: they enter their e-mail
address and the code Microsoft sends to it. The first time, the same page
signs them up. Room Booking stores no password and sends no e-mail itself.

The external tenant is a separate directory with its own app registrations,
so its access tokens differ from Entra ID's in issuer and audience:

| | Microsoft account (Entra ID) | E-mail one-time code (External ID) |
| --- | --- | --- |
| Metadata | `https://login.microsoftonline.com/common/v2.0` | `https://{subdomain}.ciamlogin.com/{tenant ID}/v2.0` |
| Issuer (`iss`) | `https://login.microsoftonline.com/{tid}/v2.0` | The issuer named in the tenant's metadata: `https://{tenant ID}.ciamlogin.com/{tenant ID}/v2.0` |
| Audience (`aud`) | `Entra:ApiClientId` | `ExternalId:ApiClientId` (the API's registration in the external tenant) |
| JWT bearer scheme | `Bearer` | `ExternalId` |
| `UserIdentities.Provider` | `Entra` | `ExternalId` |

Microsoft's guidance for an API that trusts more than one token issuer is one
JWT bearer scheme per issuer, each validating signature, issuer and audience
against its own metadata. The default scheme, `SignedIn`, is a policy scheme
that hands each token to one of them: to `ExternalId` when the token's `iss`
is an `https://….ciamlogin.com` URL, otherwise to `Bearer`. That choice reads
the token without validating it. It decides only **which** scheme validates
the token; that scheme then checks everything, and a token sent to the wrong
one is rejected.

### What an External ID token must satisfy

Code: [`ExternalIdAuthentication.cs`](../src/MeetingRoomBooking.Api/Features/Authentication/ExternalIdAuthentication.cs).

| Check | Rule | Failure |
| --- | --- | --- |
| Signature | RS256, signed with a key from the external tenant's metadata. Unsigned tokens and tokens signed with Entra ID's keys are rejected. | 401 |
| Issuer | Exactly the issuer in the tenant's metadata. Another external tenant's issuer is rejected. | 401 |
| Tenant | `tid` equals `ExternalId:TenantId`. | 401 |
| Audience | `ExternalId:ApiClientId` or its Application ID URI (`api://{client ID}` by default). The Entra registration's client ID is not accepted here. | 401 |
| Lifetime | `exp` required; up to 2 minutes of clock skew. | 401 |
| Delegated scope | `scp` contains `Entra:RequiredScope` (`access_as_user`), as for Entra tokens. | 403 |
| User | `tid` and `oid` present; a disabled user gets `403 user_disabled`. | 403 |

The identity key is the validated `iss` plus `oid`, as for Entra ID. An
External ID identity and a Microsoft account are therefore always two
identities, and two users, even with the same e-mail address or the same
object ID. Two identities are never linked automatically.

### E-mail addresses

The API **never treats an External ID token's `email` claim as verified**,
and nothing can switch that on. Microsoft's
[optional claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference)
says the claim "isn't guaranteed to be correct, and is mutable over time".
`xms_edov`, which the API uses for Entra ID, covers e-mail domains the tenant
has verified, which a gmail.com address in an external tenant never is.
Registration therefore stores no verified e-mail for an External ID identity,
and **an e-mail user cannot accept an invitation** (`403
verified_email_required`). They can sign in, register, create organisations
(as Owner) and book; to join someone else's organisation they sign in with,
or [link](api-reference.md#linking-a-second-sign-in-identity), a Microsoft
account whose verified e-mail matches the invitation.

**Why no other guarantee.** Checked against the real tenant on 29 October
2026:

- The directory does record the address the one-time code went to: the
  user's identity is `signInType: "federated"`, `issuer: "mail"`,
  `issuerAssignedId: <address>` (not `emailAddress` with the tenant domain,
  which is e-mail with password).
- The API cannot read it on the user's behalf. Through the on-behalf-of flow
  with delegated `User.Read`, the only Graph permission external tenants
  allow besides `openid` and `offline_access`, `GET /me?$select=identities`
  answered `200` with an **empty** `identities` array.
- Reading it with an app-only token (client credentials, `User.Read.All`)
  needs the separately billed **M2M Authentication** add-on of external
  tenants ([pricing](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing));
  Microsoft does not say whether Graph tokens are exempt. A paid add-on is
  outside this locally run portfolio project.
- A custom claims provider (`TokenIssuanceStart`) could put the address into
  the token from Microsoft's side, but needs a hosted HTTPS endpoint.

The restrictive rule is the safe default: an invitation is matched only
against an address Microsoft vouches for (Entra ID with `xms_edov`), never
against a token claim alone.

## Configuration

```json
"ExternalId": {
  "TenantId": "",
  "TenantSubdomain": "",
  "ApiClientId": "",
  "ApplicationIdUri": null
}
```

None of these is a secret. Without `ExternalId:ApiClientId` the API accepts
Entra ID tokens only, exactly as before. With it, the API requires
`Entra:ApiClientId` as well, because e-mail sign-in is an addition to
Microsoft sign-in. It stops at start-up when a value is invalid (a client or
tenant ID that is not a GUID, or a subdomain that is not one DNS label). The
former `ExternalId:EmailClaimIsVerified` setting was removed; a leftover
value is ignored.

Set the values without editing the file, for example with `Start-Local.ps1`:

```powershell
$env:ExternalId__TenantId = "<external tenant ID>"
$env:ExternalId__TenantSubdomain = "<subdomain, e.g. roombookingdev>"
$env:ExternalId__ApiClientId = "<client ID of the API registration in the external tenant>"
.\scripts\Start-Local.ps1 -EntraApiClientId "<client ID of the Entra API registration>"
```

## Manual steps for the owner

Everything below happens in the [Microsoft Entra admin center](https://entra.microsoft.com).
The steps follow Microsoft Learn as read on 29 October 2026 ([create an
external tenant](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-create-external-tenant-portal),
[user flows](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-sign-up-sign-in-customers),
[authentication methods](https://learn.microsoft.com/en-us/entra/external-id/customers/concept-authentication-methods-customers)).
Names in quotes are suggestions.

### 1. Create the external tenant

1. Sign in with an account that has at least the **Tenant Creator** role on
   the Azure subscription.
2. **Entra ID → Overview → Manage tenants → Create → External → Continue.**
3. Choose **Use Azure Subscription**. The **30-day free trial** needs no
   subscription, but it is a trial and expires.
4. **Basics**: Tenant name "Room Booking Customers Dev"; Domain name
   "roombookingdev" (must be unused; it becomes `roombookingdev.onmicrosoft.com`
   and the sign-in host `roombookingdev.ciamlogin.com`); Country/Region
   (cannot be changed later).
5. **Add a subscription**: the subscription, and a new resource group such as
   "rg-roombooking-externalid".
6. **Review + Create.** Creation can take up to 30 minutes.

### 2. Turn on e-mail one-time passcode and create the user flow

1. Switch to the new tenant (**Settings → Directories + subscriptions →
   Switch**).
2. **External Identities → All identity providers → Email One-time-passcode**:
   enable it for the tenant.
3. **External Identities → User flows → New user flow**: Name "SignUpSignIn";
   **Identity providers: Email Accounts → Email one-time passcode**; user
   attribute **Display Name** (under **Show more**). **Create.**

### 3. Register the API in the external tenant

1. **App registrations → New registration**: "RoomBooking-API-ExternalId-Dev",
   single tenant (external tenants offer nothing else). **Register.**
2. **Manifest**: make sure `"requestedAccessTokenVersion": 2` (older format:
   `"accessTokenAcceptedVersion": 2`), so the tokens carry the v2.0 issuer
   from the metadata.
3. **Expose an API**: Application ID URI `api://<this client ID>`; add the
   scope `access_as_user`.

### 4. Register the mobile app in the external tenant

1. **App registrations → New registration**: "RoomBooking-Mobile-ExternalId-Dev",
   single tenant; redirect URI platform **Public client/native (mobile &
   desktop)**, value `roombookingdev://auth`. **Register.**
2. **API permissions → Add a permission → APIs my organization uses →
   RoomBooking-API-ExternalId-Dev → Delegated → access_as_user.**
3. **Grant admin consent for <tenant>**, then **Refresh** and check that the
   status shows *Granted*. Users of an external tenant cannot consent
   themselves.
4. **External Identities → User flows → SignUpSignIn → Applications → Add
   application**: the mobile app. An application can belong to one user flow.

### 5. Values to hand over

Four identifiers, none of them a secret. They go into the local, ignored
configuration (`.env.local` for the app, environment variables for the API),
not into the repositories:

| Value | Where it is shown | Used as |
| --- | --- | --- |
| Tenant ID | Tenant overview | `ExternalId:TenantId`, `EXPO_PUBLIC_EXTERNAL_ID_TENANT_ID` |
| Tenant subdomain | Primary domain without `.onmicrosoft.com` | `ExternalId:TenantSubdomain`, `EXPO_PUBLIC_EXTERNAL_ID_TENANT_SUBDOMAIN` |
| API client ID | Overview of RoomBooking-API-ExternalId-Dev | `ExternalId:ApiClientId`; the app's scope `api://<id>/access_as_user` |
| Mobile client ID | Overview of RoomBooking-Mobile-ExternalId-Dev | `EXPO_PUBLIC_EXTERNAL_ID_CLIENT_ID` |

### Scripted alternative (how the development tenant was made)

On 29 October 2026 the development tenant was created and configured from
the command line instead, with the same result:

1. `az provider register -n Microsoft.AzureActiveDirectory`, a resource
   group, then the ARM call [CIAM Tenants – Create](https://learn.microsoft.com/en-us/rest/api/activedirectory/ciam-tenants/create)
   (`PUT …/providers/Microsoft.AzureActiveDirectory/ciamDirectories/roombookingdev?api-version=2023-05-17-preview`,
   `location` `Europe`, `sku` `Standard`/`A0`, `countryCode` `DK`). It
   reported billing type `MAU` and was ready within minutes.
2. `az login --tenant <new tenant ID> --allow-no-subscriptions` (one manual
   sign-in by the creator, who is Global Administrator there), in a separate
   `AZURE_CONFIG_DIR` so the normal CLI login stays untouched.
3. Microsoft Graph with `az rest`: the two app registrations and service
   principals as in steps 3–4, admin consent as `oauth2PermissionGrants`
   (`AllPrincipals`: mobile → API `access_as_user`, mobile → Graph
   `openid offline_access`), and the user flow as an
   [`externalUsersSelfServiceSignUpEventsFlow`](https://learn.microsoft.com/en-us/graph/api/identitycontainer-post-authenticationeventsflows)
   with the identity provider `EmailOtpSignup-OAUTH` only, then the mobile
   app added under `conditions/applications/includeApplications`.

The Azure CLI's Graph token could not read the authentication-methods policy
(`403`; it lacks `Policy.Read.AuthenticationMethod`). Step 2.2 was not needed:
e-mail one-time passcode worked in the user flow as created.

While testing the on-behalf-of lookup described under
[E-mail addresses](#e-mail-addresses), the API registration had a client
secret and delegated Graph `User.Read`; both were removed again afterwards,
because the API uses neither.

### Costs

From Microsoft's pricing pages as read on 29 October 2026
([pricing](https://www.microsoft.com/en-us/security/pricing/microsoft-entra-external-id),
[billing model](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)):

- Billing is by **monthly active users** (MAU), and an external tenant must be
  linked to an Azure subscription for it. "The Microsoft Entra External ID
  Basic tier includes the first 50,000 Monthly Active Users (MAU) at no cost."
  A development tenant with a handful of test users stays inside that.
- No add-on is listed for e-mail one-time passcodes. Add-ons that cost extra
  (SMS phone authentication, M2M authentication with client credentials,
  Go-Local data residency) are not used. That is also why the API does not
  read verified e-mail addresses with client credentials (see
  [E-mail addresses](#e-mail-addresses)).
- The pricing page does not state the price per MAU above 50,000, so none is
  given here.

## Verification against Microsoft

Run on 29 October 2026 against the external tenant `roombookingdev`, with the
Samsung development build and the API on an **isolated copy** of the
development database (restored from a verified backup; the real database's
SHA-256 was unchanged afterwards). "Logged" means seen in the API log, the
database copy or Graph; "on screen" means read from the phone's screen.

| # | Check | Result |
| --- | --- | --- |
| 1 | Metadata issuer is `https://{tenant ID}.ciamlogin.com/{tenant ID}/v2.0`; the API starts against it | **Pass** (logged: metadata document, API start) |
| 2 | E-mail sign-in on the phone; `GET /api/me` and registration | **Pass** (logged: `UserIdentities` row with `Provider = 'ExternalId'` and that issuer; on screen: *Logget ind med e-mail-kode*, *Du er nu registreret*) |
| 3 | Token claims | Issuer, signature, audience, `tid`, `oid` and `scp` passed the API's validation (logged); the token's `email` claim is not used, by design |
| 4 | Microsoft sign-in still works next to it | **Pass** (on screen and logged: the existing user and organisations) |
| 5 | The same person as a Microsoft account and as an e-mail login | **Two users**, never linked automatically (logged) |
| 6 | Explicit linking of the e-mail login to the Microsoft user | **Pass**: proof with a code, confirmation, then e-mail sign-in returns the Microsoft user with its memberships (logged) |
| 7 | Linking a login that is already a user of its own | **Refused**; the request was cancelled, nothing merged (logged) |
| 8 | Invitation accepted by an e-mail user | **Refused** with `verified_email_required` (logged). Seen with the intermediate on-behalf-of build, whose Graph lookup found no address; the final code refuses without any lookup, which the integration tests cover |

**Microsoft behaviour found on the way** (not Room Booking defects):

- An address that is also a **personal Microsoft account** (seen with a
  hotmail.com and a gmail.com address) gets "Denne konto findes ikke i denne
  organisation" on the sign-in page, with the sign-up link shown as raw text
  (`<aadSelfSignup>opret en ny</aadSelfSignup>`). Such an address can sign up
  on the sign-up page (`prompt=create`), after which normal sign-in works;
  other addresses get a working "Opret en" link. The app's sign-in text
  points Hotmail, Outlook and Live users to Microsoft sign-in.
- E-mail one-time-passcode identities appear in Graph as `federated` with
  issuer `mail`.

The automated tests ([`ExternalIdAuthenticationTests`](../tests/MeetingRoomBooking.Api.IntegrationTests/Authentication/ExternalIdAuthenticationTests.cs))
cover the API's rules with tokens signed by a key that exists only in the
test process; they cannot show that Microsoft issues tokens of that shape.
