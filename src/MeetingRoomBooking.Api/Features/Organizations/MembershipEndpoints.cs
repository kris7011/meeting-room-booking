namespace MeetingRoomBooking.Api.Features.Organizations;

public static class MembershipEndpoints
{
    /// <summary>
    /// …/members: the list for Owners and Admins, role changes for Owners,
    /// removal by Owners or by the member themselves (leaving).
    /// </summary>
    public static RouteGroupBuilder MapMembershipEndpoints(
        this RouteGroupBuilder organization)
    {
        organization.MapGet("/members", ListAsync)
            .RequireOwnerOrAdmin()
            .WithName("GetMembers")
            .WithTags("Organizations");

        organization.MapPut("/members/{userId:guid}/role", ChangeRoleAsync)
            .RequireOrganizationRole(MembershipRole.Owner)
            .WithName("ChangeMemberRole")
            .WithTags("Organizations");

        // Every member may call it to leave; the service lets only Owners
        // remove someone else.
        organization.MapDelete("/members/{userId:guid}", RemoveAsync)
            .WithName("RemoveMember")
            .WithTags("Organizations");

        return organization;
    }

    private static async Task<IResult> ListAsync(
        CurrentOrganization organization,
        MembershipService memberships,
        CancellationToken cancellationToken) =>
        Results.Ok(await memberships.ListAsync(organization.Required(), cancellationToken));

    private static async Task<IResult> ChangeRoleAsync(
        Guid userId,
        ChangeRoleRequest request,
        CurrentOrganization organization,
        MembershipService memberships,
        CancellationToken cancellationToken)
    {
        var result =
            await memberships.ChangeRoleAsync(organization.Required(), userId, request.Role, cancellationToken);

        return result.Member is { } member
            ? Results.Ok(member)
            : ToProblem(result);
    }

    private static async Task<IResult> RemoveAsync(
        Guid userId,
        CurrentOrganization organization,
        MembershipService memberships,
        CancellationToken cancellationToken)
    {
        var result =
            await memberships.RemoveAsync(organization.Required(), userId, cancellationToken);

        return result.Failure == MembershipFailure.None
            ? Results.NoContent()
            : ToProblem(result);
    }

    private static IResult ToProblem(
        MembershipResult result) =>
        result.Failure switch
        {
            MembershipFailure.InvalidRequest =>
                Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>()),

            MembershipFailure.MemberNotFound =>
                ApiProblems.Create(
                    StatusCodes.Status404NotFound,
                    "member_not_found",
                    "Member not found",
                    "This user is not a member of the organisation."),

            MembershipFailure.PermissionDenied =>
                ApiProblems.OrganizationPermissionDenied(
                    "Only Owners change roles and remove other members. Every member can leave."),

            MembershipFailure.LastOwner =>
                ApiProblems.Create(
                    StatusCodes.Status409Conflict,
                    "last_owner",
                    "The organisation needs an Owner",
                    "The last Owner cannot be demoted, removed or leave. Make another member Owner first."),

            _ =>
                throw new ArgumentOutOfRangeException(nameof(result), result.Failure, "Unknown membership failure.")
        };
}
