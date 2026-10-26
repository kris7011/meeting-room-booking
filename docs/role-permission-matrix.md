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
5. **Per-object rule** in the service (own booking, which invitation role, removing someone else): `403` with a specific code.

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
| `GET /bookings`, `GET /bookings/{id}`, `POST /bookings` | ✓ | ✓ | ✓ | – | `OrganizationScopedAccessTests.Members_CanBookAndChangeTheirOwnBookings_ButNotOthers` |
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
legacy organisation (`OrganizationScopedAccessTests.OriginalEndpoints_RequireALegacyMember_WhenAnonymousAccessIsOff`).

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

## Invariants

| Invariant | How | Tests |
| --- | --- | --- |
| An organisation always keeps an Owner | Service check plus database triggers `TR_Memberships_LastOwner_*` | `MemberManagementTests.OwnershipIsHandedOver_ByPromotingSomeoneElseFirst` |
| Two Owners acting on each other at once leave one Owner | `BEGIN IMMEDIATE` serialises the changes; the second sees its own new role | `MemberManagementTests.TwoOwnersRemovingEachOtherAtOnce_LeaveOneOwner`, `RolePermissionMatrixTests.TwoOwnersDemotingEachOtherAtOnce_KeepExactlyOneOwner` |
| An invitation is accepted only with its token **and** the invited verified e-mail | Token hash lookup, `xms_edov`-verified e-mail compared | `OrganizationAccessTests.VerifiedEmailOfSomeoneElse_CannotAccept`, `…UnverifiedEmail_EvenTheInvitedAddress_CannotAccept`, `…InvitedEmailWithoutTheToken_GivesNoAccess`, `MemberManagementTests.Invitation_ForAnotherAccount_IsRefused_EvenWithTheToken` |
| Expired, revoked, replaced or used invitations stop working | `410 invitation_expired` / `invitation_no_longer_valid` | `OrganizationAccessTests.ExpiredInvitation_CannotBeAccepted`, `…InvitingAgain_RevokesTheEarlierInvitation`, `…AcceptingTwice_ReturnsTheSameMembership_ButNobodyElseCanUseTheToken`, `MemberManagementTests.RevokedInvitation_CanNoLongerBeAccepted`, `…ExpiredInvitation_IsListedAsExpired_AndRefused` |
| An invitation dies with its inviter's right to send it | Checked again when accepted | `OrganizationAccessTests.InvitationOfAnInviterWhoLostTheRight_IsNoLongerValid`, `MemberManagementTests.InvitationOfARemovedAdmin_StopsWorking` |
| Parallel accepts create one membership | Primary key (`OrganizationId`, `UserId`) and a transaction | `OrganizationAccessTests.ParallelAccepts_CreateOneMembership` |
| Parallel bookings of one slot create one booking | Overlap triggers | `OrganizationScopedAccessTests.ParallelBookingsOfTheSameRoom_CreateExactlyOne`, `BookingConcurrencyTests` |
| Every denial is Problem Details with a stable `code` | `ApiProblems` | asserted by `TenantScenario.AssertProblemAsync` throughout |

## Known limitation

The role used for **room administration and booking changes** is the one the
access filter read when the request started. `MembershipService` reads the
caller's role again under the write lock, but `RoomAdministrationService` and
`BookingService` do not. A request that has already passed the filter when a
concurrent request demotes its caller can therefore still finish; the next
request is refused. The window is one request long and cannot cross an
organisation boundary.

## Not covered by these tests

Real Entra tokens and tenant metadata, and the mobile app's screens on a
device. The mobile repository records its device checks separately.
