using MeetingRoomBooking.Api.Features.Bookings;
using MeetingRoomBooking.Api.Features.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeetingRoomBooking.Api.Data;

public sealed class BookingDbContext(
    DbContextOptions<BookingDbContext> options)
    : DbContext(options)
{
    public DbSet<MeetingRoom> MeetingRooms =>
        Set<MeetingRoom>();

    public DbSet<Booking> Bookings =>
        Set<Booking>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureMeetingRooms(
            modelBuilder.Entity<MeetingRoom>());

        ConfigureBookings(
            modelBuilder.Entity<Booking>());
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

        builder.Property(room => room.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(room => room.Capacity)
            .IsRequired();

        builder.HasIndex(room => room.Name)
            .IsUnique();

        builder.HasData(
            new
            {
                Id = 1,
                Name = "Focus Room",
                Capacity = 4
            },
            new
            {
                Id = 2,
                Name = "Collaboration Room",
                Capacity = 8
            },
            new
            {
                Id = 3,
                Name = "Board Room",
                Capacity = 12
            });
    }

    private static void ConfigureBookings(
        EntityTypeBuilder<Booking> builder)
    {
        var dateTimeOffsetToUtcTicksConverter =
            new ValueConverter<DateTimeOffset, long>(
                value =>
                    value.UtcDateTime.Ticks,
                value =>
                    new DateTimeOffset(
                        value,
                        TimeSpan.Zero));

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
                dateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(booking => booking.EndUtc)
            .HasConversion(
                dateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(booking => booking.CreatedUtc)
            .HasConversion(
                dateTimeOffsetToUtcTicksConverter)
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.HasOne(booking => booking.Room)
            .WithMany(room => room.Bookings)
            .HasForeignKey(booking => booking.RoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(
            booking => new
            {
                booking.RoomId,
                booking.StartUtc,
                booking.EndUtc
            });
    }
}