# Microsoft Entra External ID: e-mail one-time-code sign-in

How the API accepts sign-ins with an e-mail address and a one-time passcode
through Microsoft Entra External ID, how to configure it, and the manual
steps to create the external tenant. Microsoft account sign-in (Entra ID) is
unchanged and described in [entra-setup.md](entra-setup.md); the decision is
recorded in [ADR 0008](adr/0008-external-id-as-a-second-token-issuer.md).

**Status.** The API side is **implemented and covered by integration tests**
that sign tokens with a test key in the documented External ID format. It has
**not been run against a real external tenant**: none exists yet. Everything
under [Verification against Microsoft](#verification-against-microsoft) is
still open.

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

An External ID identity has **no verified e-mail** by default. Microsoft
documents that e-mail accounts in an external tenant prove their address with
a one-time passcode, but not that the `email` claim in the API's access token
is that address. `xms_edov`, which the API uses for Entra ID, covers e-mail
domains the tenant has verified, which an outlook.com or gmail.com address in
an external tenant never is. Without a verified e-mail, an External ID user
can sign in, register, create organisations and book, but **cannot accept an
invitation** (`403 verified_email_required`).

`ExternalId:EmailClaimIsVerified: true` makes the API treat the `email` claim
of External ID tokens as verified. The API then adds its own marker identity
to the principal; a claim in the token itself can never mark an address as
verified. Turn it on only after [checking a real token](#verification-against-microsoft)
and only while the user flow offers e-mail accounts alone (no Google,
Facebook or other federated providers).

## Configuration

```json
"ExternalId": {
  "TenantId": "",
  "TenantSubdomain": "",
  "ApiClientId": "",
  "ApplicationIdUri": null,
  "EmailClaimIsVerified": false
}
```

None of these is a secret. Without `ExternalId:ApiClientId` the API accepts
Entra ID tokens only, exactly as before. With it, the API requires
`Entra:ApiClientId` as well, because e-mail sign-in is an addition to
Microsoft sign-in. It stops at start-up when a value is invalid (a client or
tenant ID that is not a GUID, or a subdomain that is not one DNS label).

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

### Costs

From Microsoft's pricing pages as read on 29 October 2026
([pricing](https://www.microsoft.com/en-us/security/pricing/microsoft-entra-external-id),
[billing model](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)):

- Billing is by **monthly active users** (MAU), and an external tenant must be
  linked to an Azure subscription for it. "The Microsoft Entra External ID
  Basic tier includes the first 50,000 Monthly Active Users (MAU) at no cost."
  A development tenant with a handful of test users stays inside that.
- No add-on is listed for e-mail one-time passcodes. Add-ons that cost extra
  (SMS phone authentication, machine-to-machine tokens, Go-Local data
  residency) are not used.
- The pricing page does not state the price per MAU above 50,000, so none is
  given here.

## Verification against Microsoft

Not done; each needs the external tenant:

1. The tenant's metadata names the issuer `https://{tenant ID}.ciamlogin.com/{tenant ID}/v2.0`
   and the API starts against it.
2. A real e-mail sign-in on the phone: `GET /api/me` answers `provider:
   "ExternalId"`; registration creates a `UserIdentities` row with
   `Provider = 'ExternalId'`.
3. The access token's claims: `tid`, `oid`, `scp` and `name` present, `ver`
   2.0. Whether an `email` claim can be issued, and whether it equals the
   address the code went to, decides `EmailClaimIsVerified`.
4. Microsoft account sign-in still works next to it (regression).

The automated tests ([`ExternalIdAuthenticationTests`](../tests/MeetingRoomBooking.Api.IntegrationTests/Authentication/ExternalIdAuthenticationTests.cs))
cover the API's rules with tokens signed by a key that exists only in the
test process; they cannot show that Microsoft issues tokens of that shape.
