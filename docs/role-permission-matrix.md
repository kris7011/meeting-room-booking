# Role and permission matrix

Who may call which organisation endpoint, where the API enforces it, and
which integration test proves it. The API is the only authority: the mobile
app hides actions a role cannot use, but every rule below holds for a raw
HTTP request. Background: [ADR 0001](adr/0001-organisation-scoped-access-control.md)
and [walkthrough section 6](PROJECT_WALKTHROUGH.md#6-authorization-and-multi-tenancy-tested).

Test classes are in `tests/MeetingRoomBooking.Api.IntegrationTests/Organizations/`
unless a path is given. They run the real API (`WebApplicationFactory`) on a
temporary SQLite file with tokens signed by a test-only key
(`EntraTestTokens`); none of them touches a real database or a real tenant.

## How a request is checked

1. **Token** (`SignedInUserPolicy`): none or invalid → `401`.
2. **User** (`OrganizationAccessService.ResolveUserAsync`): not registered → `403 user_not_registered`; disabled → `403 user_disabled`.
3. **Membership** (`OrganizationAccessFilter`): not a member, or no such organisation → `404 organization_not_found` (the two are indistinguishable).
4. **Role** (`OrganizationRoleRequirement` metadata, from `RequireOwnerOrAdmin` / `RequireOrganizationRole`): `403 organization_permission_denied`.
5. **Per-object rule** in the service (own booking, which invitation role, removing someone else): `403` with a specific code. Reading a booking is never refused inside the organisation, but a Member gets another person's booking without its title and booked-by ([ADR 0006](adr/0006-booking-details-hidden-from-members.md)).
6. **Again under the write lock** (`OrganizationAccessService.ReauthorizeAsync`): every change repeats steps 2–4 inside its `BEGIN IMMEDIATE` transaction, before it reads or writes anything else, and step 5 uses the role read there. A caller disabled, removed or demoted while the request was running gets the same answer as in steps 2–4 and nothing is written ([ADR 0004](adr/0004-reauthorize-mutations-under-the-write-lock.md)).

Roles are read from the database on every request, never from the token.
`RolePermissionMatrixTests.EveryOrganizationEndpoint_RequiresExactlyTheRolesOfTheMatrix`
reads the API's route table and fails if an endpoint is added, removed or
given other roles without updating the matrix below.

## Endpoints under `/api/organizations/{organizationId}`

O = Owner, A = Admin, M = Member. "Filter" is step 4, "service" step 5. The
route-table test above checks the filter roles of every row; the tests listed
per row send real requests. Where a row lists no request test for a role
(for example a Member reading `GET /`), only the route-table test covers it.

| Endpoint | O | A | M | Enforced by | Tests (allowed / denied) |
| --- | --- | --- | --- | --- | --- |
| `GET /` details | ✓ | ✓ | ✓ | – | `OrganizationManagementTests.LegacyOrganization_KeepsItsRoomsAndOwner` |
| `PUT /` rename | ✓ (not Legacy) | – | – | filter: Owner; service: Legacy read-only (`409 legacy_organization_read_only`) | `OrganizationManagementTests.OnlyOwners_CanRenameAnOrganization_AndNeverTheLegacyOne` (Owner, Admin); `RolePermissionMatrixTests.Member_CannotRestoreRoomsRenameOrChangeAnotherMembersRole` (Member) |
| `GET /rooms`, `GET /rooms/available` | ✓ | ✓ | ✓ | `?includeArchived=true`: Owner/Admin in the endpoint | `OrganizationManagementTests.OwnersAndAdmins_ManageRooms_MembersOnlyUseThem`, `…ArchivingARoom_KeepsPastBookings_AndRemovesItFromSearchAndBooking` |
| `POST /rooms` | ✓ | ✓ | – | filter: Owner, Admin | `OrganizationManagementTests.OwnersAndAdmins_ManageRooms_MembersOnlyUseThem` |
| `PUT /rooms/{roomId}` | ✓ | ✓ | – | filter: Owner, Admin | `OrganizationManagementTests.EditingARoom_KeepsItsBookings` (Admin); `…OwnersAndAdmins_ManageRooms_MembersOnlyUseThem` (Member) |
| `POST /rooms/{roomId}/archive` | ✓ | ✓ | – | filter: Owner, Admin; service: no upcoming bookings | `OrganizationManagementTests.ArchivingARoom_KeepsPastBookings_AndRemovesItFromSearchAndBooking`; `…OwnersAndAdmins_ManageRooms_MembersOnlyUseThem` (Member) |
| `POST /rooms/{roomId}/restore` | ✓ | ✓ | – | filter: Owner, Admin; service: name still free | `OrganizationManagementTests.ArchivingARoom_…` (Admin), `…RestoringARoom_WhoseNameIsTakenAgain_IsRefused`; `RolePermissionMatrixTests.Member_CannotRestoreRooms…` (Member) |
| `GET /bookings`, `GET /bookings/{id}` | all, in full | all, in full | all; title and booked-by only of their own | service: `BookingActor.MaySeeDetails` (`detailsHidden: true`, `title`/`bookedBy` null) | `BookingPrivacyTests` (all), `OrganizationScopedAccessTests.Members_CanBookAndChangeTheirOwnBookings_ButNotOthers` |
| `POST /bookings` | ✓ | ✓ | ✓ | – | `OrganizationScopedAccessTests.Members_CanBookAndChangeTheirOwnBookings_ButNotOthers` |
| `PUT`/`DELETE /bookings/{id}` | any | any | own only | service: `BookingActor.MayModify` (`403 booking_permission_denied`) | `OrganizationScopedAccessTests.Members_CanBookAndChangeTheirOwnBookings_ButNotOthers`, `…OwnersAndAdmins_CanChangeAndDeleteEveryBookingOfTheirOrganization`, `…BookingsFromBeforeSignIn_CanOnlyBeChangedByOwnersAndAdmins` |
| `GET /members` | ✓ | ✓ | – | filter: Owner, Admin | `MemberManagementTests.OwnersAndAdmins_SeeTheMembers_MembersDoNot` |
| `PUT /members/{userId}/role` | ✓ | – | – | filter: Owner; service: re-reads the caller's role under the write lock, last Owner | `MemberManagementTests.Owner_ChangesRoles_AndTheChangeIsAudited`, `…Admin_CannotMakeThemselvesOrAnyoneOwner`; `RolePermissionMatrixTests.Member_CannotRestoreRoomsRenameOrChangeAnotherMembersRole` |
| `DELETE /members/{userId}` | anyone | self only | self only | service: only Owners remove others; last Owner | `MemberManagementTests.Owner_RemovesAMember_WhoLosesAccessButKeepsTheirBookings`, `…AdminsAndMembers_CannotRemoveOthers_ButAnyoneCanLeave` |
| `GET /invitations` | ✓ | ✓ | – | filter: Owner, Admin; never returns the token or its hash | `MemberManagementTests.OpenInvitations_AreListedWithoutTheirTokens` |
| `POST /invitations` | Admin, Member | Member | – | service (`InvitationService.MayInvite`); never Owner (`400`) | `OrganizationAccessTests.Admin_MayInviteMembersButNotAdmins`, `…Member_CannotInvite`, `…OwnershipAndUnknownRoles_CannotBeInvited` |
| `DELETE /invitations/{id}` | any | Member invitations | – | filter: Owner, Admin; service: `MayInvite` | `MemberManagementTests.Admins_RevokeOnlyMemberInvitations`, `…RevokedInvitation_CanNoLongerBeAccepted` |

`POST /api/organizations` (create) and `GET /api/organizations` (list) need a
registered, enabled user and no membership; the list contains only the
caller's own memberships (`OrganizationManagementTests.AUser_CanBeAMemberOfSeveralOrganizations`,
`RolePermissionMatrixTests.NonMembers_CannotTellAnExistingOrganizationFromAMissingOne`).
The original `/api/rooms` and `/api/bookings` apply the same checks to the
legacy organisation (`OrganizationScopedAccessTests.OriginalEndpoints_RequireALegacyMember_WhenAnonymousAccessIsOff`),
including the hidden booking details, there as null values in the unchanged response shape
(`BookingPrivacyTests.OriginalEndpoints_HideTheSameFields_FromSignedInMembers_InTheirFixedShape`).

## Callers without access

| Caller | Result | Tests |
| --- | --- | --- |
| No token, malformed or wrongly signed token | `401` on every organisation endpoint | `OrganizationScopedAccessTests.EveryOrganizationEndpoint_RejectsAnonymousCallersAndNonMembers`, `OrganizationAccessTests.InvitationEndpoints_RequireAValidToken` |
| Valid token, not registered | `403 user_not_registered` | `OrganizationScopedAccessTests.UnregisteredAndDisabledUsers_AreRefused`, `OrganizationManagementTests.CreatingRequiresARegisteredSignedInUser`, `OrganizationAccessTests.UnregisteredUser_CannotInviteOrAccept` |
| Disabled user | `403 user_disabled`, also for list and create | `OrganizationScopedAccessTests.UnregisteredAndDisabledUsers_AreRefused`, `OrganizationAccessTests.DisabledOwner_CannotInvite`, `…DisabledInvitee_CannotAccept`, `RolePermissionMatrixTests.DisabledUser_CannotListOrCreateOrganizations` |
| Registered non-member (any role elsewhere) | `404 organization_not_found` on every endpoint, identical to an unknown organisation; nothing written | `OrganizationScopedAccessTests.EveryOrganizationEndpoint_RejectsAnonymousCallersAndNonMembers`, `RolePermissionMatrixTests.NonMembers_CannotTellAnExistingOrganizationFromAMissingOne` |
| Member of another organisation using this organisation's room, booking or invitation IDs | `404` (`meeting_room_not_found`, `booking_not_found`, `invitation_not_found`); nothing changed | `OrganizationScopedAccessTests.MemberOfOneOrganization_CannotSeeAnotherOrganizationsRoomsOrBookings`, `…CannotChangeOrDeleteAnotherOrganizationsBookings`, `OrganizationManagementTests.RoomsOfAnotherOrganization_CannotBeManaged`, `MemberManagementTests.InvitationsOfAnotherOrganization_CannotBeRevoked`, `…UsersOutsideTheOrganization_AreNotMembersToChangeOrRemove` |
| Removed member, same token | `404` at once; their bookings are kept | `MemberManagementTests.Owner_RemovesAMember_WhoLosesAccessButKeepsTheirBookings` |
| Demoted Admin, same token | Member rights from the next request on | `RolePermissionMatrixTests.DemotedAdmin_LosesTheAdminRightsOnTheNextRequest_WithTheSameToken` |
| Disabled, removed or demoted **while a change is running** (after the filter, before its transaction) | Same answer as a request sent afterwards (`403 user_disabled`, `404 organization_not_found`, `403 organization_permission_denied` / `booking_permission_denied`); no row and no audit event written | `MutationReauthorizationTests` (rooms ×4 changes ×3 revocations, bookings incl. the original `/api/bookings`, rename, invitation revoke, role change, remove) |
| Removed or disabled, then **repeats a booking with its Idempotency-Key** (before or while it waits for the write lock) | Same `404`/`403` as any request; the stored booking is never returned | `BookingIdempotencyTests.Retry_AfterAccessWasRevoked_IsRefusedWithoutTheBooking`, `…Retry_WhoseAccessIsRevokedWhileItWaitsForTheLock_IsRefused` |
| Another member or another organisation reuses someone's Idempotency-Key | A new request in their own scope (a taken slot is `409 booking_overlap`); never the other caller's booking | `BookingIdempotencyTests.Keys_AreScopedToTheUser`, `…Keys_AreScopedToTheOrganization` |

## Invariants

| Invariant | How | Tests |
| --- | --- | --- |
| An organisation always keeps an Owner | Service check plus database triggers `TR_Memberships_LastOwner_*` | `MemberManagementTests.OwnershipIsHandedOver_ByPromotingSomeoneElseFirst` |
| Two Owners acting on each other at once leave one Owner | `BEGIN IMMEDIATE` serialises the changes; the second sees its own new role | `MemberManagementTests.TwoOwnersRemovingEachOtherAtOnce_LeaveOneOwner`, `RolePermissionMatrixTests.TwoOwnersDemotingEachOtherAtOnce_KeepExactlyOneOwner`, `MutationReauthorizationTests.OwnerDemotedByTheOtherOwnerMidRequest_CannotDemoteThemBack` |
| The access check and the write of a change cannot be separated by another writer | The check runs inside the change's `BEGIN IMMEDIATE` transaction | `MutationReauthorizationTests.ChangeHoldingTheWriteLock_IsWrittenBeforeARevocationCanBegin` |
| An invitation is accepted only with its token **and** the invited verified e-mail | Token hash lookup, `xms_edov`-verified e-mail compared | `OrganizationAccessTests.VerifiedEmailOfSomeoneElse_CannotAccept`, `…UnverifiedEmail_EvenTheInvitedAddress_CannotAccept`, `…InvitedEmailWithoutTheToken_GivesNoAccess`, `MemberManagementTests.Invitation_ForAnotherAccount_IsRefused_EvenWithTheToken` |
| Expired, revoked, replaced or used invitations stop working | `410 invitation_expired` / `invitation_no_longer_valid` | `OrganizationAccessTests.ExpiredInvitation_CannotBeAccepted`, `…InvitingAgain_RevokesTheEarlierInvitation`, `…AcceptingTwice_ReturnsTheSameMembership_ButNobodyElseCanUseTheToken`, `MemberManagementTests.RevokedInvitation_CanNoLongerBeAccepted`, `…ExpiredInvitation_IsListedAsExpired_AndRefused` |
| An invitation dies with its inviter's right to send it | Checked again when accepted | `OrganizationAccessTests.InvitationOfAnInviterWhoLostTheRight_IsNoLongerValid`, `MemberManagementTests.InvitationOfARemovedAdmin_StopsWorking` |
| Parallel accepts create one membership | Primary key (`OrganizationId`, `UserId`) and a transaction | `OrganizationAccessTests.ParallelAccepts_CreateOneMembership` |
| Parallel bookings of one slot create one booking | Overlap triggers | `OrganizationScopedAccessTests.ParallelBookingsOfTheSameRoom_CreateExactlyOne`, `BookingConcurrencyTests` |
| Every denial is Problem Details with a stable `code` | `ApiProblems` | asserted by `TenantScenario.AssertProblemAsync` throughout |

## Known limitation

Closed in Phase 5B: until then, room administration, booking changes,
renaming and invitation revocation used the role the access filter read when
the request started, so a request already past the filter when its caller was
demoted or removed could still write. Every organisation change now checks
again under the write lock (step 6 above). **Reads** still use the snapshot
from the start of the request: a list fetched while the caller is being
removed shows what they could see a moment earlier, and changes nothing.

## Not covered by these tests

Real Entra tokens and tenant metadata, and the mobile app's screens on a
device. The mobile repository records its device checks separately.
