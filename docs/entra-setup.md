# Microsoft Entra ID: token validation and setup

How the API validates Microsoft access tokens, how to configure it, and the
manual steps in the Entra admin center. The mobile app's side is in
[room-booking-mobile/docs/entra-login.md](https://github.com/kris7011/room-booking-mobile/blob/main/docs/entra-login.md);
the Angular client's in its [README](../src/meeting-room-booking-web/README.md).

## Sign-in with Microsoft Entra ID

The API validates Microsoft Entra ID access tokens itself and decides from them who the caller is. A client never tells the API who the user is: a name, e-mail or user ID sent in a request body is never trusted for identity.

Two kinds of Microsoft account can sign in:

- **Work and school accounts** (Microsoft Entra ID) of any tenant, or of the tenants in `Entra:AllowedTenantIds`.
- **Personal Microsoft accounts** (Outlook.com, Hotmail, Live.com), unless `Entra:AllowPersonalAccounts` is `false`. Their tokens come from Microsoft's consumer tenant `9188040d-6c67-4c5b-b112-36a304b66dad` and are validated exactly like work and school tokens, with that tenant as `tid`.

What is in place:

- `GET /api/me` requires a valid access token and returns the caller as the API sees them: display name, `preferred_username`, tenant ID and, once the identity is registered, the API's own user (`publicId`, display name, and the organisations the user belongs to with their role). It is read-only and creates no users or identities.
- `POST /api/me/registration` registers the signed-in identity as a Room Booking user; see [Registration and organisation access](api-reference.md#registration-and-organisation-access).
- Every room and booking endpoint requires a signed-in member of the organisation; see [Organisation access control](api-reference.md#organisation-access-control).
- Without `Entra:ApiClientId` the API starts as before, and `GET /api/me` answers `503` with `code: "authentication_not_configured"`.

### What a token must satisfy

| Check | Rule | Failure |
| --- | --- | --- |
| Signature | RS256, signed with a key from the Microsoft identity platform metadata: `https://login.microsoftonline.com/common/v2.0`, or `…/organizations/v2.0` when personal accounts are off. Only `common` also publishes the keys personal-account tokens are signed with. Unsigned (`alg: none`) and HMAC tokens are rejected. | 401 |
| Signing key issuer | A key that the metadata publishes for a specific issuer may only sign tokens of that issuer (`EnableAadSigningKeyIssuerValidation`). The personal-account keys name the consumer tenant's issuer, so they never validate a work or school token. | 401 |
| Issuer | Exactly `https://login.microsoftonline.com/{tid}/v2.0`, where `{tid}` is the token's own `tid` claim, a GUID; for a personal account `https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0`. Endpoint aliases (`common`, `organizations`, `consumers`) are never an issuer. v1.0 tokens (`https://sts.windows.net/{tid}/`) are rejected, so one person never gets two issuers. | 401 |
| Tenant | The consumer tenant when `Entra:AllowPersonalAccounts` is `true` (the default). Any other tenant, or only those in `Entra:AllowedTenantIds` when it is set. | 401 |
| Audience | The API's client ID or its Application ID URI (`api://{client ID}` by default). | 401 |
| Lifetime | `exp` is required; up to 2 minutes of clock skew. | 401 |
| Delegated scope | `scp` contains `Entra:RequiredScope` (`access_as_user`). App-only tokens (roles, no `scp`) never pass as a user. | 403 |
| User | `tid` and `oid` are present. A linked user whose account is disabled gets `403` with `code: "user_disabled"`. | 403 |

The identity key is the validated `iss` together with `oid`, the same key as the unique index on `UserIdentities (Issuer, Subject)`. `sub` is not used, because it differs between the app's ID token and the API's access token, and the e-mail address is never a key. A personal account and a work account are therefore always different identities, even with the same e-mail address, because their issuers differ.

### Configuration

```json
"Entra": {
  "Instance": "https://login.microsoftonline.com",
  "ApiClientId": "",
  "RequiredScope": "access_as_user",
  "AllowedTenantIds": [],
  "AllowPersonalAccounts": true
}
```

`AllowedTenantIds` limits work and school accounts only; personal accounts are switched on and off with `AllowPersonalAccounts` alone.

None of these values is a secret: the API validates tokens and never requests one, so it has no client secret. Set the API's client ID without editing the file, for example:

```powershell
$env:Entra__ApiClientId = "<Application (client) ID of the API registration>"
dotnet run --project .\src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj --launch-profile http
```

The API stops at start-up if a value is set but invalid (a client ID or tenant ID that is not a GUID, the consumer tenant in `AllowedTenantIds`, an instance that is not HTTPS, or more than one scope).

### Manual steps in the Microsoft Entra admin center

The API needs its own app registration, separate from the mobile app's:

1. **App registrations → New registration**, for example "Room Booking API". Supported account types: *Accounts in any organizational directory and personal Microsoft accounts* (`"signInAudience": "AzureADandPersonalMicrosoftAccount"`). Without personal accounts: *Accounts in any organizational directory*, or *this directory only* together with `Entra:AllowedTenantIds`, and `Entra:AllowPersonalAccounts` set to `false`.
2. **Manifest**: set `"requestedAccessTokenVersion": 2` (in the older manifest format `"accessTokenAcceptedVersion": 2`). Otherwise Entra issues v1.0 tokens, which this API rejects. Entra requires version 2 before personal accounts can be switched on, so for an existing registration set this first, then change `signInAudience` in the manifest.
3. **Expose an API**: set the Application ID URI to `api://<API client ID>` and add the scope `access_as_user` (who can consent: admins and users).
4. **Token configuration → Add optional claim → Access**: `email` and `xms_edov` (accept the prompt to add the Microsoft Graph `email` permission). Only needed for accepting invitations; see [Verified e-mail addresses](api-reference.md#verified-e-mail-addresses).
5. In the **mobile app's** registration: **API permissions → Add a permission → My APIs → Room Booking API → Delegated → access_as_user**, and the same supported account types as the API. Grant admin consent if the tenant does not allow user consent.
6. For users of other tenants: an administrator of each tenant must consent to the mobile app (and with it the API) before its users can sign in. Personal accounts consent for themselves on their first sign-in.

### Not done yet

- Linking a second identity (for example a work account and a personal account) to the same user.
- CORS (Development only) allows the headers `Accept`, `Content-Type` and `Idempotency-Key`, not `Authorization`. The Angular client sends its token to its own origin, which the development server proxies to the API, so it needs no CORS; a browser client on another origin could not send `Authorization`. Native apps are not affected.
