# ADR 0007: Browser sign-in for the Angular client

- **Status:** Accepted. Implemented and unit-tested; a real Microsoft sign-in
  in the browser needs the SPA redirect URI in Entra and has not been run yet.
- **Code:** `src/meeting-room-booking-web/src/app/core/auth/`
  (`auth-config.ts`, `msal-client.ts`, `auth.service.ts`, `auth.interceptor.ts`),
  `core/services/account-api.service.ts`, `core/services/booking-api.service.ts`, `app.ts`

## Context

The plan agreed on 21 October 2026 keeps the original Angular client and says
it gets its own Microsoft sign-in later. Until now it only called the original
`/api/rooms` and `/api/bookings` without a token, which works only in the API's
Development demo mode. The organisation features (and every rule of
[ADR 0001](0001-organisation-scoped-access-control.md)) were reachable only
from the mobile app.

## Decision

- **MSAL Browser (`@azure/msal-browser`), authorization code flow with PKCE,
  public client, by redirect.** This is Microsoft's library for single-page
  apps. `@azure/msal-angular` is not used: its current release requires a
  `msal-browser` version published the day before this decision, and the
  client needs only a small part of it. A narrow `MsalClient` interface keeps
  MSAL behind one file, so `AuthService` is tested with a fake.
- **Redirect, not pop-up.** Pop-ups are blocked by browsers and are awkward on
  phones; the page reloads once after sign-in, which a booking form can afford.
- **Tokens in `sessionStorage`.** They end with the tab. `localStorage` would
  keep a refresh token across browser restarts, and any XSS on the origin could
  read either; the shorter lifetime limits that window.
- **Token only to the own origin.** The development server proxies `/api` to
  the API, so the browser never calls another origin. `authInterceptor`
  attaches the token only to relative `/api/…` URLs; an absolute URL never
  gets it. The API's CORS policy therefore stays as it is.
- **Settings at run time, from `public/auth-config.json` (ignored by Git).**
  Client ID, tenant and API scope are identifiers, not secrets, but they belong
  to one installation. Without the file the client runs in demo mode as before,
  so a reviewer can run it without an app registration.
- **MSAL loaded lazily**, only when the settings file exists, so the demo bundle
  stays small (initial bundle about 249 kB; MSAL is a separate 307 kB chunk).
- **Scope of the signed-in client: booking.** Choose an organisation, see its
  rooms and bookings, create, change and delete bookings where the API allows.
  Creating organisations, rooms, invitations and roles stays in the mobile app,
  which already has those screens and their tests; a user without an
  organisation is told so.
- **The API stays the authority.** The client follows `canModify` and
  `detailsHidden`; it does not decide anything itself. A late answer for an
  organisation the user has switched away from is ignored.

## Alternatives considered

- **No browser sign-in; document the Angular client as demo-only.** Rejected:
  it contradicts the agreed plan.
- **A backend-for-frontend with a cookie session** (tokens never in the
  browser). Stronger against XSS, but it needs a confidential client with a
  secret, server-side session storage and CSRF protection. Better for a
  production web app; too much for this local portfolio deployment.
- **Expo Web sign-in instead.** The mobile web build is used for E2E tests and
  its redirect handling is native-first; the owner decided on 21 October that
  Expo Web gets no sign-in.

## Consequences

- The client registration needs an SPA redirect URI per origin
  (`http://localhost:4200` locally). Until it exists, Microsoft refuses the
  redirect and the client cannot sign in; demo mode is unaffected.
- A production build has no proxy. Serving it would need the API behind the
  same origin (reverse proxy) or a CORS policy that allows `Authorization`;
  neither is part of this repository (there is no deployment).
- Not automated: a real sign-in against Entra, and the redirect itself.
