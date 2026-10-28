# Room Booking web client (Angular)

The browser client of the Room Booking API: a schedule of bookings with a
form to create, change and delete them. Angular 21 standalone component,
Signals, Reactive Forms, RxJS, Tailwind CSS 4, Vitest.

It has two modes:

| Mode | When | Data |
| --- | --- | --- |
| **Demo** | No `public/auth-config.json`, or signed out | The original `/api/rooms` and `/api/bookings` of the legacy organisation, without a token. Only an API in its Development demo mode (`LegacyApi__AllowAnonymous=true`) answers them. |
| **Signed in** | `auth-config.json` present and signed in with Microsoft | `/api/organizations/{id}/…` of the chosen organisation, with a bearer token. The API checks membership and role on every request. |

Signed in, the client books for an organisation: choose one of your
organisations, see its rooms and bookings, and create, change or delete
bookings where the API says you may (`canModify`). A Member sees other
people's bookings as "Booked", without title or organiser (the API removes
them; backend ADR 0006). Creating organisations, rooms, invitations and
roles is done in the [mobile app](https://github.com/kris7011/room-booking-mobile);
the reason is in [ADR 0007](../../docs/adr/0007-browser-sign-in-for-the-angular-client.md).

## Run

```bash
npm ci
npm start              # http://localhost:4200, /api proxied to http://localhost:5003
npm test -- --watch=false
npm run build
```

Start the API first (see the [repository README](../../README.md)). The
development server proxies `/api` to the API (`proxy.conf.json`), so the
browser only talks to its own origin and no CORS configuration is needed.

## Microsoft sign-in (optional)

1. In the Entra admin center, open the **client** app registration (the
   public client the mobile app uses, or a new one) and add the platform
   **Single-page application** with the redirect URI `http://localhost:4200`.
   It needs the delegated permission `access_as_user` on the API registration
   and the same supported account types as the API.
2. Copy `auth-config.example.json` to `public/auth-config.json` (ignored by
   Git) and fill in the client ID and the API's scope. These are identifiers,
   not secrets.
3. Start the API with `Entra__ApiClientId` set, restart `npm start`, and use
   **Sign in with Microsoft**.

How it works ([ADR 0007](../../docs/adr/0007-browser-sign-in-for-the-angular-client.md)):

- `@azure/msal-browser` runs the authorization code flow with PKCE as a public
  client, by redirect. It is loaded only when `auth-config.json` exists, so
  the demo mode does not download it.
- Tokens are cached in `sessionStorage` and end with the tab.
- `authInterceptor` adds the access token only to relative `/api/…` requests,
  never to an absolute URL.
- On the first visit the client asks `GET /api/me` and registers the user with
  `POST /api/me/registration` if needed. Registration grants nothing: a user
  without an organisation is told to create one or accept an invitation in the
  mobile app.

## Structure

```text
src/app/
  app.ts, app.html               The page: account panel, form and schedule
  app.config.ts                  HTTP client with the auth interceptor; sign-in initialised before start-up
  core/auth/                     auth-config (settings file), msal-client (MSAL wrapper), AuthService, authInterceptor
  core/services/                 BookingApiService (legacy or organisation scope), AccountApiService (/api/me, organisations)
  core/models/                   API contracts
```

## Tests

Vitest through the Angular CLI (`npm test`). Services are tested with
Angular's `HttpTestingController`; `AuthService` with a fake MSAL client, so
no test reaches Microsoft; the component with stubbed services, including the
signed-in organisation flow, hidden booking details and a late answer from an
organisation the user switched away from.

Not automated: a real Microsoft sign-in in a browser.
