using MeetingRoomBooking.Api.Features.Audit;
using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.Features.Organizations;
using MeetingRoomBooking.Api.Features.Rooms;
using MeetingRoomBooking.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeetingRoomBooking.Api.Data;

public sealed class BookingDbContext(
    DbContextOptions<BookingDbContext> options)
    : DbContext(options)
{
    // Every point in time is stored as UTC ticks in an INTEGER column, so
    // SQLite can compare and index them.
    private static readonly ValueConverter<DateTimeOffset, long>
        DateTimeOffsetToUtcTicksConverter =
            new(
                value =>
                    value.UtcDateTime.Ticks,
                value =>
                    new DateTimeOffset(
                        value,
                        TimeSpan.Zero));

    public DbSet<Organization> Organizations =>
        Set<Organization>();

    public DbSet<User> Users =>
        Set<User>();

    public DbSet<MeetingRoom> MeetingRooms =>
        Set<MeetingRoom>();

    public DbSet<Booking> Bookings =>
        Set<Booking>();

    public DbSet<UserIdentity> UserIdentities =>
        Set<UserIdentity>();

    public DbSet<Membership> Memberships =>
        Set<Membership>();

    public DbSet<Invitation> Invitations =>
        Set<Invitation>();

    public DbSet<AuditEvent> AuditEvents =>
        Set<AuditEvent>();

    public DbSet<BookingCreationRequest> BookingCreationRequests =>
        Set<BookingCreationRequest>();

    public DbSet<IdentityLinkRequest> IdentityLinkRequests =>
        Set<IdentityLinkRequest>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUsers(
            modelBuilder.Entity<User>());

        ConfigureOrganizations(
            modelBuilder.Entity<Organization>());

        ConfigureMeetingRooms(
            modelBuilder.Entity<MeetingRoom>());

        ConfigureBookings(
            modelBuilder.Entity<Booking>());

        ConfigureUserIdentities(
            modelBuilder.Entity<UserIdentity>());

        ConfigureMemberships(
            modelBuilder.Entity<Membership>());

        ConfigureInvitations(
            modelBuilder.Entity<Invitation>());

        ConfigureAuditEvents(
            modelBuilder.Entity<AuditEvent>());

        ConfigureBookingCreationRequests(
            modelBuilder.Entity<BookingCreationRequest>());

        ConfigureIdentityLinkRequests(
            modelBuilder.Entity<IdentityLinkRequest>());
    }

    private static void ConfigureBookingCreationRequests(
        EntityTypeBuilder<BookingCreationRequest> builder)
    {
        builder.ToTable(
            "BookingCreationRequests",
            tableBuilder =>
                // Only a lower-case hex SHA-256 fits.
                tableBuilder.HasCheckConstraint(
                    "CK_BookingCreationRequests_RequestHash",
                    $"length(\"RequestHash\") = {BookingCreationRequest.RequestHashLength} AND \"RequestHash\" NOT GLOB '*[^0-9a-f]*'"));

        builder.HasKey(request => request.Id);

        builder.Property(request => request.RequestHash)
            .HasMaxLength(BookingCreationRequest.RequestHashLength)
            .IsRequired();

        builder.Property(request => request.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(request => request.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting the booking keeps the key used: a repeat is told the
        // booking is gone instead of creating it again.
        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(request => request.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        // One booking per key, caller and organisation. SQLite treats NULLs
        // as distinct, so anonymous demo-mode keys get their own index.
        builder.HasIndex(
                request => new
                {
                    request.OrganizationId,
                    request.UserId,
                    request.IdempotencyKey
                })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL")
            .HasDatabaseName("UX_BookingCreationRequests_Organization_User_Key");

        builder.HasIndex(
                request => new
                {
                    request.OrganizationId,
                    request.IdempotencyKey
                })
            .IsUnique()
            .HasFilter("\"UserId\" IS NULL")
            .HasDatabaseName("UX_BookingCreationRequests_Organization_Key_Anonymous");

        // The retention clean-up.
        builder.HasIndex(request => request.CreatedUtc)
            .HasDatabaseName("IX_BookingCreationRequests_CreatedUtc");

        builder.HasIndex(request => request.BookingId)
            .HasDatabaseName("IX_BookingCreationRequests_BookingId");
    }

    private static void ConfigureUserIdentities(
        EntityTypeBuilder<UserIdentity> builder)
    {
        builder.ToTable(
            "UserIdentities",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "CK_UserIdentities_Provider",
                    "\"Provider\" IN ('Entra', 'ExternalId')");

                tableBuilder.HasCheckConstraint(
                    "CK_UserIdentities_IssuerAndSubject",
                    "length(\"Issuer\") > 0 AND length(\"Subject\") > 0");

                tableBuilder.HasCheckConstraint(
                    "CK_UserIdentities_VerifiedEmail",
                    "\"VerifiedEmail\" IS NULL OR (\"VerifiedEmail\" = lower(trim(\"VerifiedEmail\")) AND instr(\"VerifiedEmail\", '@') > 1)");
            });

        builder.HasKey(identity => identity.Id);

        builder.Property(identity => identity.Provider)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(identity => identity.Issuer)
            .HasMaxLength(UserIdentity.MaximumIssuerLength)
            .IsRequired();

        builder.Property(identity => identity.Subject)
            .HasMaxLength(UserIdentity.MaximumSubjectLength)
            .IsRequired();

        builder.Property(identity => identity.VerifiedEmail)
            .HasMaxLength(UserIdentity.MaximumEmailLength);

        builder.Property(identity => identity.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(identity => identity.LastUsedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One identity belongs to exactly one user.
        builder.HasIndex(
                identity => new
                {
                    identity.Issuer,
                    identity.Subject
                })
            .IsUnique()
            .HasDatabaseName("UX_UserIdentities_Issuer_Subject");

        // Deliberately not unique: the same e-mail on two identities does not
        // make them the same person.
        builder.HasIndex(identity => identity.VerifiedEmail)
            .HasDatabaseName("IX_UserIdentities_VerifiedEmail");
    }

    private static void ConfigureMemberships(
        EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable(
            "Memberships",
            tableBuilder =>
                tableBuilder.HasCheckConstraint(
                    "CK_Memberships_Role",
                    "\"Role\" IN ('Owner', 'Admin', 'Member')"));

        // At most one membership per user and organisation.
        builder.HasKey(
            membership => new
            {
                membership.OrganizationId,
                membership.UserId
            });

        builder.Property(membership => membership.Role)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(membership => membership.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(membership => membership.UpdatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Which organisations am I in" and "who owns this organisation".
        builder.HasIndex(membership => membership.UserId)
            .HasDatabaseName("IX_Memberships_UserId");

        builder.HasIndex(
                membership => new
                {
                    membership.OrganizationId,
                    membership.Role
                })
            .HasDatabaseName("IX_Memberships_OrganizationId_Role");
    }

    private static void ConfigureIdentityLinkRequests(
        EntityTypeBuilder<IdentityLinkRequest> builder)
    {
        builder.ToTable(
            "IdentityLinkRequests",
            tableBuilder =>
            {
                // Only a lower-case hex SHA-256 fits: a raw code is rejected.
                tableBuilder.HasCheckConstraint(
                    "CK_IdentityLinkRequests_CodeHash",
                    $"length(\"CodeHash\") = {IdentityLinkCode.HashLength} AND \"CodeHash\" NOT GLOB '*[^0-9a-f]*'");

                tableBuilder.HasCheckConstraint(
                    "CK_IdentityLinkRequests_Expiry",
                    "\"ExpiresUtc\" > \"CreatedUtc\"");

                // The claiming identity is recorded completely or not at all.
                tableBuilder.HasCheckConstraint(
                    "CK_IdentityLinkRequests_Claim",
                    "(\"ClaimedUtc\" IS NULL AND \"ClaimedProvider\" IS NULL AND \"ClaimedIssuer\" IS NULL AND \"ClaimedSubject\" IS NULL AND \"ClaimedVerifiedEmail\" IS NULL) OR " +
                    "(\"ClaimedUtc\" IS NOT NULL AND \"ClaimedProvider\" IN ('Entra', 'ExternalId') AND length(\"ClaimedIssuer\") > 0 AND length(\"ClaimedSubject\") > 0)");

                // Completed only after a claim, and never also cancelled.
                tableBuilder.HasCheckConstraint(
                    "CK_IdentityLinkRequests_Outcome",
                    "(\"CompletedUtc\" IS NULL OR \"ClaimedUtc\" IS NOT NULL) AND (\"CompletedUtc\" IS NULL OR \"CancelledUtc\" IS NULL)");
            });

        builder.HasKey(request => request.Id);

        builder.Property(request => request.CodeHash)
            .HasMaxLength(IdentityLinkCode.HashLength)
            .IsRequired();

        builder.Property(request => request.ClaimedProvider)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(request => request.ClaimedIssuer)
            .HasMaxLength(UserIdentity.MaximumIssuerLength);

        builder.Property(request => request.ClaimedSubject)
            .HasMaxLength(UserIdentity.MaximumSubjectLength);

        builder.Property(request => request.ClaimedVerifiedEmail)
            .HasMaxLength(UserIdentity.MaximumEmailLength);

        builder.Property(request => request.CreatedUtc)
            .HasConversion(DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(request => request.ExpiresUtc)
            .HasConversion(DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(request => request.ClaimedUtc)
            .HasConversion(DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.Property(request => request.CompletedUtc)
            .HasConversion(DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.Property(request => request.CancelledUtc)
            .HasConversion(DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(request => request.PublicId)
            .IsUnique()
            .HasDatabaseName("UX_IdentityLinkRequests_PublicId");

        builder.HasIndex(request => request.CodeHash)
            .IsUnique()
            .HasDatabaseName("UX_IdentityLinkRequests_CodeHash");

        builder.HasIndex(request => request.UserId)
            .HasDatabaseName("IX_IdentityLinkRequests_UserId");
    }

    private static void ConfigureInvitations(
        EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable(
            "Invitations",
            tableBuilder =>
            {
                // An invitation never grants ownership.
                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_Role",
                    "\"Role\" IN ('Admin', 'Member')");

                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_Email",
                    "\"Email\" = lower(trim(\"Email\")) AND instr(\"Email\", '@') > 1");

                // Only a lower-case hex SHA-256 fits: a raw token is rejected.
                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_TokenHash",
                    $"length(\"TokenHash\") = {InvitationToken.HashLength} AND \"TokenHash\" NOT GLOB '*[^0-9a-f]*'");

                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_Expiry",
                    "\"ExpiresUtc\" > \"CreatedUtc\"");

                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_Accepted",
                    "(\"AcceptedByUserId\" IS NULL) = (\"AcceptedUtc\" IS NULL)");

                tableBuilder.HasCheckConstraint(
                    "CK_Invitations_AcceptedOrRevoked",
                    "\"AcceptedUtc\" IS NULL OR \"RevokedUtc\" IS NULL");
            });

        builder.HasKey(invitation => invitation.Id);

        builder.Property(invitation => invitation.Email)
            .HasMaxLength(Invitation.MaximumEmailLength)
            .IsRequired();

        builder.Property(invitation => invitation.Role)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(invitation => invitation.TokenHash)
            .HasMaxLength(InvitationToken.HashLength)
            .IsRequired();

        builder.Property(invitation => invitation.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(invitation => invitation.ExpiresUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(invitation => invitation.AcceptedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.Property(invitation => invitation.RevokedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(invitation => invitation.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(invitation => invitation.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(invitation => invitation.AcceptedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(invitation => invitation.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_Invitations_TokenHash");

        // One open invitation per e-mail and organisation; the same address
        // can be invited to other organisations.
        builder.HasIndex(
                invitation => new
                {
                    invitation.OrganizationId,
                    invitation.Email
                })
            .IsUnique()
            .HasFilter("\"AcceptedUtc\" IS NULL AND \"RevokedUtc\" IS NULL")
            .HasDatabaseName("UX_Invitations_OrganizationId_Email_Open");
    }

    private static void ConfigureAuditEvents(
        EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        builder.HasKey(auditEvent => auditEvent.Id);

        builder.Property(auditEvent => auditEvent.Type)
            .HasConversion<string>()
            .HasMaxLength(AuditEvent.MaximumTypeLength)
            .IsRequired();

        builder.Property(auditEvent => auditEvent.OccurredUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(
                auditEvent => new
                {
                    auditEvent.OrganizationId,
                    auditEvent.OccurredUtc
                })
            .HasDatabaseName("IX_AuditEvents_OrganizationId_OccurredUtc");
    }

    private static void ConfigureUsers(
        EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.PublicId)
            .IsRequired();

        builder.Property(user => user.DisplayName)
            .HasMaxLength(User.MaximumDisplayNameLength)
            .IsRequired();

        builder.Property(user => user.ContactEmail)
            .HasMaxLength(User.MaximumContactEmailLength);

        builder.Property(user => user.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(user => user.LastSeenUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.Property(user => user.DisabledUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasIndex(user => user.PublicId)
            .IsUnique()
            .HasDatabaseName("UX_Users_PublicId");
    }

    private static void ConfigureOrganizations(
        EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable(
            "Organizations",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "CK_Organizations_Name",
                    $"length(\"Name\") BETWEEN {Organization.MinimumNameLength} AND {Organization.MaximumNameLength}");

                tableBuilder.HasCheckConstraint(
                    "CK_Organizations_LegacyHasNoCreator",
                    "\"IsLegacy\" = 0 OR \"CreatedByUserId\" IS NULL");

                tableBuilder.HasCheckConstraint(
                    "CK_Organizations_CreationRequest",
                    "(\"CreationRequestId\" IS NULL) = (\"CreationRequestHash\" IS NULL)");
            });

        builder.HasKey(organization => organization.Id);

        builder.Property(organization => organization.PublicId)
            .IsRequired();

        builder.Property(organization => organization.Name)
            .HasMaxLength(Organization.MaximumNameLength)
            .IsRequired();

        builder.Property(organization => organization.IsLegacy)
            .IsRequired();

        builder.Property(organization => organization.CreationRequestHash)
            .HasMaxLength(Organization.CreationRequestHashLength);

        builder.Property(organization => organization.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(organization => organization.DeletedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(organization => organization.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(organization => organization.PublicId)
            .IsUnique()
            .HasDatabaseName("UX_Organizations_PublicId");

        // At most one legacy organisation can exist.
        builder.HasIndex(organization => organization.IsLegacy)
            .IsUnique()
            .HasFilter("\"IsLegacy\" = 1")
            .HasDatabaseName("UX_Organizations_IsLegacy");

        // Decides concurrent retries of the same creation request. SQLite
        // treats NULLs as distinct, so organisations without a key never clash.
        builder.HasIndex(
                organization => new
                {
                    organization.CreatedByUserId,
                    organization.CreationRequestId
                })
            .IsUnique()
            .HasDatabaseName("UX_Organizations_CreatedByUserId_CreationRequestId");

        builder.HasIndex(
                organization => new
                {
                    organization.CreatedByUserId,
                    organization.CreatedUtc
                })
            .HasDatabaseName("IX_Organizations_CreatedByUserId_CreatedUtc");

        builder.HasData(
            new
            {
                Id = LegacyOrganization.Id,
                PublicId = LegacyOrganization.PublicId,
                Name = LegacyOrganization.Name,
                IsLegacy = true,
                CreatedUtc = LegacyOrganization.CreatedUtc
            });
    }

    private static void ConfigureMeetingRooms(
        EntityTypeBuilder<MeetingRoom> builder)
    {
        builder.ToTable(
            "MeetingRooms",
            tableBuilder =>
                tableBuilder.HasCheckConstraint(
                    "CK_MeetingRooms_Capacity",
                    "\"Capacity\" > 0"));

        builder.HasKey(room => room.Id);

        // Target of the bookings' (OrganizationId, RoomId) foreign key.
        builder.HasAlternateKey(
            room => new
            {
                room.OrganizationId,
                room.Id
            });

        builder.Property(room => room.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(room => room.Capacity)
            .IsRequired();

        builder.Property(room => room.ArchivedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(room => room.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Names are unique within an organisation; an archived room frees
        // its name.
        builder.HasIndex(
                room => new
                {
                    room.OrganizationId,
                    room.Name
                })
            .IsUnique()
            .HasFilter("\"ArchivedUtc\" IS NULL")
            .HasDatabaseName("UX_MeetingRooms_OrganizationId_Name");

        // The demo rooms belong to the legacy organisation only; organisations
        // created later start without rooms.
        builder.HasData(
            new
            {
                Id = 1,
                OrganizationId = LegacyOrganization.Id,
                Name = "Focus Room",
                Capacity = 4
            },
            new
            {
                Id = 2,
                OrganizationId = LegacyOrganization.Id,
                Name = "Collaboration Room",
                Capacity = 8
            },
            new
            {
                Id = 3,
                OrganizationId = LegacyOrganization.Id,
                Name = "Board Room",
                Capacity = 12
            });
    }

    private static void ConfigureBookings(
        EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable(
            "Bookings",
            tableBuilder =>
                tableBuilder.HasCheckConstraint(
                    "CK_Bookings_TimeRange",
                    "\"EndUtc\" > \"StartUtc\""));

        builder.HasKey(booking => booking.Id);

        builder.Property(booking => booking.Title)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(booking => booking.BookedBy)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(booking => booking.StartUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(booking => booking.EndUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(booking => booking.CreatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(booking => booking.UpdatedUtc)
            .HasConversion(
                DateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER");

        // A booking always belongs to its room's organisation.
        builder.HasOne(booking => booking.Room)
            .WithMany(room => room.Bookings)
            .HasForeignKey(
                booking => new
                {
                    booking.OrganizationId,
                    booking.RoomId
                })
            .HasPrincipalKey(
                room => new
                {
                    room.OrganizationId,
                    room.Id
                })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(booking => booking.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(booking => booking.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Used by the overlap triggers and the availability search.
        builder.HasIndex(
            booking => new
            {
                booking.RoomId,
                booking.StartUtc,
                booking.EndUtc
            });

        builder.HasIndex(
            booking => new
            {
                booking.OrganizationId,
                booking.StartUtc
            });
    }
}
