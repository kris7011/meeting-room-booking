# Meeting Room Booking

A small fullstack application for creating and managing meeting room reservations.

The project was developed as a technical assignment with emphasis on clear structure, understandable business rules, useful error handling and automated tests.

## Features

The application supports:

- Viewing available meeting rooms.
- Viewing existing bookings.
- Creating bookings.
- Updating bookings.
- Deleting bookings.
- Selecting a meeting room.
- Client-side form validation.
- Server-side validation.
- Prevention of overlapping bookings.
- Clear validation, not-found and conflict messages.
- Responsive desktop and mobile layouts.
- Persistent local storage through SQLite.

## Technology stack

### Backend

- .NET 8
- ASP.NET Core Minimal API
- Entity Framework Core 8
- SQLite
- Swagger/OpenAPI

### Frontend

- Angular 21
- TypeScript
- Angular Reactive Forms
- Angular Signals
- RxJS
- Tailwind CSS 4
- PostCSS

### Testing and automation

- xUnit
- ASP.NET Core `WebApplicationFactory`
- SQLite based integration tests
- Vitest
- Angular HTTP testing utilities
- GitHub Actions

## Project structure

```text
meeting-room-booking/
│
├── .github/
│   └── workflows/
│       └── ci.yml
│
├── src/
│   ├── MeetingRoomBooking.Api/
│   │   ├── Data/
│   │   │   ├── Migrations/
│   │   │   ├── BookingDbContext.cs
│   │   │   └── DatabaseExtensions.cs
│   │   │
│   │   ├── Features/
│   │   │   ├── Bookings/
│   │   │   │   ├── Booking.cs
│   │   │   │   ├── BookingContracts.cs
│   │   │   │   ├── BookingEndpoints.cs
│   │   │   │   └── BookingService.cs
│   │   │   │
│   │   │   └── Rooms/
│   │   │       ├── MeetingRoom.cs
│   │   │       └── RoomEndpoints.cs
│   │   │
│   │   ├── MeetingRoomBooking.Api.http
│   │   └── Program.cs
│   │
│   └── meeting-room-booking-web/
│       ├── .postcssrc.json
│       ├── proxy.conf.json
│       ├── package.json
│       │
│       └── src/
│           ├── app/
│           │   ├── core/
│           │   │   ├── models/
│           │   │   └── services/
│           │   │
│           │   ├── app.ts
│           │   ├── app.html
│           │   ├── app.css
│           │   └── app.spec.ts
│           │
│           └── styles.css
│
├── tests/
│   └── MeetingRoomBooking.Api.IntegrationTests/
│
├── global.json
├── MeetingRoomBooking.sln
├── .gitignore
└── README.md
```

The backend uses a feature oriented structure. Code related to bookings is kept together instead of being distributed across large technical folders.

The frontend keeps shared API models and services inside the `core` folder, while application state and user-interface behaviour are handled by the root Angular component.

## Domain model

### Meeting room

A meeting room contains:

- An identifier.
- A name.
- A capacity.

The application is seeded with the following rooms:

| Room               | Capacity |
| ------------------ | -------: |
| Focus Room         |        4 |
| Collaboration Room |        8 |
| Board Room         |       12 |

### Booking

A booking contains:

- An identifier.
- A meeting room identifier.
- A title.
- The name of the organiser.
- A start time.
- An end time.
- A creation timestamp.

The `Booking` entity validates its own core invariants, including required values, maximum text lengths and valid time ranges.

## API endpoints

```text
GET    /health

GET    /api/rooms

GET    /api/bookings
GET    /api/bookings/{id}
POST   /api/bookings
PUT    /api/bookings/{id}
DELETE /api/bookings/{id}
```

Swagger is available in the Development environment at:

```text
http://localhost:5003/swagger
```

## Example booking request

```json
{
  "roomId": 1,
  "title": "Sprint planning",
  "bookedBy": "Kris Larsen",
  "startUtc": "2030-01-10T09:00:00Z",
  "endUtc": "2030-01-10T10:00:00Z"
}
```

## Overlap rule

A meeting room cannot have two bookings whose time periods overlap.

Two bookings overlap when:

```text
existing.StartUtc < requested.EndUtc
AND
existing.EndUtc > requested.StartUtc
```

This means adjacent bookings are allowed.

```text
Existing booking: 09:00-10:00
New booking:      10:00-11:00
```

These bookings do not overlap.

The overlap rule is checked both when a booking is created and when an existing booking is updated.

During an update, the booking being changed is excluded from its own overlap query. This prevents a booking from being treated as a conflict with itself.

The rule is enforced by the API rather than only by the frontend. This ensures that the rule still applies when requests come from clients other than the Angular application.

## Error handling

Expected API errors use appropriate HTTP status codes and Problem Details responses.

### `400 Bad Request`

Used for invalid input, including:

- Missing title.
- Missing organiser.
- Invalid meeting-room identifier.
- Missing start or end time.
- End time earlier than or equal to start time.
- Text values that exceed the allowed maximum length.

### `404 Not Found`

Used when:

- A requested booking does not exist.
- A selected meeting room does not exist.

### `409 Conflict`

Used when the selected room already has an overlapping booking.

Example:

```json
{
  "type": "about:blank",
  "title": "Meeting room is unavailable",
  "status": 409,
  "detail": "The meeting room is already booked during the selected period.",
  "code": "booking_overlap"
}
```

The Angular frontend translates expected API errors into clear user-facing messages.

## Data storage

SQLite was selected because it provides real database behaviour while keeping the project easy to run locally.

It demonstrates:

- Entity Framework Core configuration.
- Entity relationships.
- Database migrations.
- Persistent data.
- Relational constraints.
- Real overlap queries.
- Database backed integration tests.

The local SQLite database file is excluded from Git.

The database schema is recreated from Entity Framework Core migrations, and the API automatically applies pending migrations when it starts.

## Date and time handling

The browser form uses `datetime-local`, which represents a local date and time without timezone information.

Before a booking request is sent, Angular converts the local value into an ISO UTC timestamp using `toISOString()`.

The API normalises and stores booking timestamps as UTC values.

When an existing booking is edited, its UTC timestamp is converted back into the local format expected by the `datetime-local` input.

For a larger production system, the organisation or meeting room timezone should also be modelled explicitly.

## Frontend architecture

### Reactive Forms

Angular Reactive Forms manage:

- Current form values.
- Required field validation.
- Maximum text lengths.
- Date range validation.
- Touched and invalid states.
- Create and edit modes.

The form uses a custom validator to ensure that the end time is later than the start time.

Frontend validation improves the user experience, but backend validation remains authoritative.

### Angular Signals

Angular Signals manage local user interface state, including:

- Meeting rooms.
- Bookings.
- Loading state.
- Saving state.
- Editing state.
- Delete confirmation.
- Error messages.
- Success messages.

Computed signals provide derived state, including the current form heading and whether the application is in create or edit mode.

### RxJS Observables

The API service returns RxJS Observables for all HTTP operations.

The application uses:

- `subscribe()` to receive HTTP responses.
- `forkJoin()` to load rooms and bookings in parallel.
- `finalize()` to reset loading and saving states after successful or failed requests.

### API service

HTTP communication is centralised in `BookingApiService`.

The component does not need to know the implementation details of:

- API URLs.
- HTTP methods.
- Request types.
- Response types.

The service supports:

```text
getRooms
getBookings
getBooking
createBooking
updateBooking
deleteBooking
```

### Tailwind CSS

The user interface is styled with Tailwind CSS 4 utility classes.

Tailwind is processed through PostCSS using the configuration in:

```text
src/meeting-room-booking-web/.postcssrc.json
```

The Tailwind import is located in:

```text
src/meeting-room-booking-web/src/styles.css
```

The interface includes responsive layouts for desktop, tablet and mobile screen sizes.

## Prerequisites

Install:

- .NET 8 SDK.
- Node.js 22.
- npm.

The repository contains a `global.json` file that keeps the solution on the .NET 8 SDK family.

Verify the active SDK:

```powershell
dotnet --version
```

The output should start with:

```text
8.0.
```

Verify Node.js:

```powershell
node --version
```

The recommended version is:

```text
v22.
```

## Restore dependencies

From the repository root:

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet restore `
    .\MeetingRoomBooking.sln

npm --prefix `
    .\src\meeting-room-booking-web `
    ci
```

## Run the application

The backend and frontend run as separate development processes.

### Terminal 1 - API

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet run `
    --project .\src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj `
    --launch-profile http
```

The API runs at:

```text
http://localhost:5003
```

Swagger is available at:

```text
http://localhost:5003/swagger
```

### Terminal 2 - Angular frontend

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix `
    .\src\meeting-room-booking-web `
    start
```

Open the application at:

```text
http://localhost:4200
```

The Angular development server proxies requests beginning with `/api` to the backend on port `5003`.

The proxy configuration is located in:

```text
src/meeting-room-booking-web/proxy.conf.json
```

## Build and test

### Backend

Restore and build the solution:

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet restore `
    .\MeetingRoomBooking.sln

dotnet build `
    .\MeetingRoomBooking.sln `
    --configuration Release `
    --no-restore
```

Run the backend test suite:

```powershell
dotnet test `
    .\MeetingRoomBooking.sln `
    --configuration Release `
    --no-build
```

The backend suite currently contains 15 integration tests.

### Frontend

Install the locked frontend dependencies:

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix `
    .\src\meeting-room-booking-web `
    ci
```

Create a production build:

```powershell
npm --prefix `
    .\src\meeting-room-booking-web `
    run build
```

Run the frontend tests:

```powershell
npm --prefix `
    .\src\meeting-room-booking-web `
    test -- --watch=false
```

The frontend suite currently contains 8 tests:

- 6 API-service tests.
- 2 application-component tests.

## Testing strategy

### Backend tests

Backend integration tests start the real ASP.NET Core application through `WebApplicationFactory`.

Each test factory uses an isolated temporary SQLite database.

Connection pooling is disabled in the test configuration so Windows can reliably release and delete the temporary database file after each test.

Backend scenarios include:

- Database migration and seeded meeting rooms.
- Retrieval of meeting rooms.
- Retrieval of bookings.
- Retrieval of a booking by identifier.
- Valid booking creation.
- Booking persistence.
- Overlap rejection.
- The same period in different rooms.
- Adjacent bookings.
- Invalid time ranges.
- Unknown meeting rooms.
- Valid updates.
- Updates without self conflict.
- Conflicting updates.
- Unknown bookings.
- Successful deletion.
- Deletion of missing bookings.

### Frontend service tests

Frontend API service tests verify:

- Request URL.
- HTTP method.
- Request body.
- Returned response.
- Completion of delete requests.
- Absence of unexpected HTTP requests.

The tests use Angular's HTTP testing utilities rather than starting the real backend.

### Frontend component tests

Application component tests verify:

- That the Angular application can be created.
- That rooms and bookings are loaded.
- That existing bookings are rendered.
- That selecting Edit populates the booking form.

## Continuous integration

GitHub Actions runs automatically for:

- Pushes to `main`.
- Pull requests.

The workflow runs backend and frontend validation as separate jobs.

```text
Backend:
restore => Release build => integration tests

Frontend:
npm ci => production build => unit tests
```

The workflow is located at:

```text
.github/workflows/ci.yml
```

Separating the jobs makes it clear whether a failure belongs to the backend or frontend.

## Design decisions

### SQLite rather than an in-memory collection

SQLite provides persistent data, migrations, constraints and real database queries without requiring a separate database server.

It also allows integration tests to use the same database provider as the running application.

### Feature oriented backend structure

Booking entities, contracts, endpoints and services are grouped together.

This makes the code for each feature easier to locate and understand.

### Backend owned business rules

The frontend performs validation to improve the user experience, but the API remains authoritative for validity and overlap rules.

This prevents business rules from being bypassed by another frontend or API client.

### Read-only EF Core queries

Queries that materialise entities only for display use `AsNoTracking()`.

This avoids unnecessary EF Core change tracking overhead.

Update and delete operations use tracked entities so EF Core can detect changes and generate the required SQL statements when `SaveChangesAsync()` is called.

### Problem Details responses

Expected API errors use structured Problem Details responses with application specific error codes.

The frontend can therefore distinguish between overlap conflicts, missing resources and validation errors.

### Angular API service

HTTP calls are centralised in `BookingApiService`.

Angular components do not need to know endpoint addresses or HTTP implementation details.

### Local list updates

After successful create, update and delete operations, the frontend updates its local booking signal directly.

This avoids issuing an additional full GET request after every mutation.

### Tailwind utility classes

Most visual styling is expressed through Tailwind utility classes close to the associated HTML.

The global stylesheet is limited to the Tailwind import and a small number of application wide browser styles.

### UTC storage

Booking times are stored as UTC values to avoid tying stored data to the timezone of the machine running the API.

## Current test results

At the time of completion, the project passes:

```text
Backend:
15 passed
0 failed

Frontend:
8 passed
0 failed
```

The Angular production build and the .NET Release build both complete successfully.

## Possible improvements

With more development time, the following could be added:

- Authentication and authorisation.
- Identification of the currently signed in user.
- Calendar style schedule view.
- Filtering by room or date.
- Recurring bookings.
- Pagination.
- Audit logging.
- Optimistic concurrency handling.
- More advanced timezone modelling.
- End-to-end browser tests.
- Docker support.
- Deployment configuration.
- Structured telemetry.