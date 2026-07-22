# Meeting Room Booking

A small fullstack application for managing meeting room bookings.

The project is being developed as a technical assignment. The purpose is to demonstrate how I structure a small but non trivial application, implement business rules, handle errors and connect an API with a frontend.

The assignment is intentionally kept focused. The main priority is clear, maintainable and testable code rather than a large number of features.

## Technology stack

### Backend

- .NET 8
- ASP.NET Core Minimal API
- Entity Framework Core 8
- SQLite

### Frontend

- Angular 21
- TypeScript
- Angular Reactive Forms
- CSS

Tailwind CSS may be added as part of the frontend implementation.

### Testing

- xUnit
- ASP.NET Core `WebApplicationFactory`
- SQLite based integration tests
- Vitest for Angular tests

## Project structure

```text
meeting-room-booking/
│
├── src/
│   ├── MeetingRoomBooking.Api/
│   │   └── ASP.NET Core API
│   │
│   └── meeting-room-booking-web/
│       └── Angular frontend
│
├── tests/
│   └── MeetingRoomBooking.Api.IntegrationTests/
│       └── Backend integration tests
│
├── global.json
├── MeetingRoomBooking.sln
├── .gitignore
└── README.md
```

## Requirements

The application will support:

- Viewing existing bookings.
- Creating a booking.
- Updating an existing booking.
- Deleting a booking.
- Selecting a meeting room.
- Displaying understandable validation and conflict errors.

The central business rule is:

> A meeting room cannot be booked when another booking for the same room overlaps the requested time period.

## Planned domain model

### Meeting room

A meeting room contains information such as:

- Unique identifier.
- Name.
- Capacity.

### Booking

A booking contains:

- Unique identifier.
- Meeting room identifier.
- Title.
- Name of the person who created the booking.
- Start time.
- End time.

## Planned API endpoints

```text
GET    /api/rooms
GET    /api/bookings
GET    /api/bookings/{id}

POST   /api/bookings
PUT    /api/bookings/{id}
DELETE /api/bookings/{id}
```

The final endpoint design may be adjusted slightly during implementation when the domain and use cases become clearer.

## Overlap rule

Two bookings overlap when the existing booking starts before the new booking ends and the existing booking ends after the new booking starts.

Conceptually:

```text
existing.Start < requested.End
AND
existing.End > requested.Start
```

This allows one booking to begin exactly when another booking ends.

Example:

```text
Existing booking: 09:00-10:00
New booking:      10:00-11:00
```

These bookings do not overlap.

The overlap check must also be applied when an existing booking is updated. During an update, the booking being changed must be excluded from its own overlap check.

## Error handling

The API will return clear HTTP responses for expected errors.

Examples:

```text
400 Bad Request
```

Used when input is invalid, for example:

- The title is missing.
- The end time is earlier than the start time.
- The selected meeting room is invalid.

```text
404 Not Found
```

Used when a requested room or booking does not exist.

```text
409 Conflict
```

Used when a meeting room is already booked during the requested period.

Example response:

```json
{
  "code": "booking_overlap",
  "message": "The meeting room is already booked during the selected period."
}
```

The Angular frontend will display these messages in a user-friendly form.

## Data storage

SQLite was selected because it provides real database behaviour while remaining simple to run locally.

Compared with an in-memory collection, SQLite makes it possible to demonstrate:

- Entity Framework Core configuration.
- Database migrations.
- Persistent data.
- Database backed integration tests.
- Real queries for detecting overlapping bookings.

The local SQLite database file is excluded from Git. The database schema will instead be recreated from Entity Framework Core migrations.

## Date and time handling

The API will store booking times as UTC values.

The frontend will allow the user to enter local date and time values and convert them before sending the request to the API.

This avoids tying stored data to the timezone of the machine running the application.

For a production system, the timezone associated with the meeting room or organisation should also be defined explicitly.

## Getting started

### Prerequisites

Install:

- .NET 8 SDK.
- Node.js.
- npm.

The repository contains a `global.json` file that keeps the project on the .NET 8 SDK family.

Verify the active SDK:

```powershell
dotnet --version
```

The output should begin with:

```text
8.0.
```

## Restore dependencies

From the repository root:

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet restore .\MeetingRoomBooking.sln

npm --prefix .\src\meeting-room-booking-web install
```

## Build the backend

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet build .\MeetingRoomBooking.sln
```

## Run backend tests

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet test .\MeetingRoomBooking.sln
```

## Run the API

```powershell
cd "C:\Dev\meeting-room-booking"

dotnet run `
    --project .\src\MeetingRoomBooking.Api\MeetingRoomBooking.Api.csproj
```

The exact local API address is displayed in the terminal when the application starts.

## Build the frontend

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix .\src\meeting-room-booking-web run build
```

## Run frontend tests

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix .\src\meeting-room-booking-web test -- --watch=false
```

## Run the frontend

```powershell
cd "C:\Dev\meeting-room-booking"

npm --prefix .\src\meeting-room-booking-web start
```

The Angular development server is normally available at:

```text
http://localhost:4200
```

## Current status

The initial solution structure has been created.

Completed:

- .NET 8 solution.
- ASP.NET Core API project.
- Entity Framework Core SQLite packages.
- Backend integration test project.
- Angular standalone application.
- Initial build configuration.

Next implementation steps:

1. Add meeting room and booking entities.
2. Add the Entity Framework Core database context.
3. Add initial meeting room data.
4. Add database migrations.
5. Implement booking CRUD endpoints.
6. Implement booking overlap detection.
7. Add API integration tests.
8. Build the Angular booking list and form.
9. Display validation and overlap errors in the frontend.
10. Add final documentation and automated CI checks.

## Testing strategy

The most important behaviour will be covered by integration tests against the API.

The tests will verify scenarios such as:

- A valid booking can be created.
- A booking can be retrieved.
- A booking can be updated.
- A booking can be deleted.
- Overlapping bookings for the same room are rejected.
- The same time period can be used for different rooms.
- Adjacent bookings are accepted.
- Invalid time periods are rejected.
- Updating a booking does not conflict with itself.
- Updating a booking into another booking's time period is rejected.

Frontend tests will focus on:

- Form validation.
- Correct API requests.
- Display of existing bookings.
- Display of conflict and validation errors.

## Design considerations

The project will favour a feature oriented structure instead of placing every class of the same technical type into large shared folders.

For example:

```text
Features/
  Bookings/
    Booking.cs
    BookingEndpoints.cs
    BookingRepository.cs
    BookingService.cs

  Rooms/
    MeetingRoom.cs
    MeetingRoomEndpoints.cs
```

This keeps the code belonging to a feature close together and makes a small application easier to navigate.

Business rules that are important to the domain should not be hidden inside the frontend. The API remains responsible for enforcing the overlap rule, because requests may come from clients other than the Angular application.

The frontend may perform basic validation to improve the user experience, but backend validation remains authoritative.

## Possible improvements with more time

With more development time, the following could be added:

- Authentication and authorisation.
- Identification of the currently logged in user.
- Calendar style booking view.
- Filtering by room and date.
- Recurring bookings.
- Booking cancellation history.
- Audit logging.
- Optimistic concurrency handling.
- Pagination.
- Accessibility review.
- More advanced timezone handling.
- Docker support.
- Deployment configuration.
- Structured application telemetry.
- End-to-end browser tests.
- CI/CD deployment pipeline.