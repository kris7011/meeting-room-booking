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
