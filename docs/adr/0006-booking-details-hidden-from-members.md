# ADR 0006: Booking titles and bookers hidden from Members

- **Status:** Accepted
- **Code:** `Features/Bookings/BookingService.cs` (`BookingActor.MaySeeDetails`),
  `Features/Bookings/BookingContracts.cs` (`BookingResponse.FromBooking`,
  `OrganizationBookingResponse.FromBooking`), `Features/Bookings/BookingEndpoints.cs`
- **Tests:** `Organizations/BookingPrivacyTests.cs`

## Context

The architecture plan agreed on 21 October 2026 says that ordinary members
should, by default, not see the title or the booker of other people's
bookings. They may see the room, the time and whether it is free; only Owners
and authorised administrators see meeting details, and the API must enforce
this. Until this decision was implemented, every member received every
booking of the organisation in full, and the mobile app listed them.

A meeting title can be sensitive ("Restructuring", a candidate's name), and
"booked by" says who meets when. Members still need to know that a room is
taken, or they cannot understand why it is missing from a search.

## Decision

- `BookingActor.MaySeeDetails(booking)`: Owners and Admins see every
  booking's details; a Member only those of bookings they created. Bookings
  from before sign-in have no creator, so Members do not see theirs.
- The rule is applied where a booking becomes a response, in
  `FromBooking`, so every endpoint that returns a booking uses it.
- Organisation endpoints: `title` and `bookedBy` are `null` and
  `detailsHidden` is `true`. Room, period, `canModify` and
  `createdByCurrentUser` stay, so a client can show "booked" and nothing more.
- Original `/api/bookings` endpoints: their response shape is fixed, so a
  hidden booking has `title` and `bookedBy` set to `null` and no new property.
- Anonymous demo mode (Development and Testing only) has no caller and shows
  every booking in full, as before.
- The role comes from the database on every request, as for every other
  check, so a promotion or demotion applies to the next request.

## Alternatives considered

- **Hide other people's bookings completely.** Members would then see a room
  as missing from search results without any reason. Rejected: the plan
  explicitly allows the room and time.
- **A separate "busy times" endpoint for Members.** Clearer separation, but
  a second contract for the same data, and clients would need to know which
  one to call per role. Rejected for now.
- **Filter in the client.** The data would still reach the device and any
  other HTTP client. Rejected: the API must enforce it.
- **A per-organisation setting to show titles to everyone.** Deliberately not
  added; the agreed default is private. It can be added later without
  changing this contract.

## Consequences

- Clients must handle `null` title and booked-by. The mobile app shows such a
  booking as "Optaget" with its room and time.
- `MaySeeDetails` and `MayModify` contain the same rule today. They are kept
  apart because seeing and changing are different permissions and may diverge.
- Reads use the role from the start of the request (see the known
  limitation in [role-permission-matrix.md](../role-permission-matrix.md#known-limitation)):
  an Admin demoted to Member while a `GET` runs may still get that response
  in full.
- No migration or stored data changes; only responses change.
