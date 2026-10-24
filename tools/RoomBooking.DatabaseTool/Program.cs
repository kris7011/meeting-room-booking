using MeetingRoomBooking.Api.Data;

static void Usage()
{
    Console.Error.WriteLine(
        """
        Room Booking database portability (.NET 10)

          backup  --database <existing.db> --directory <backup-folder>
          restore --from <verified-backup.db> --to <NEW-database.db>

        Restore never overwrites an existing database.
        Copy a portable backup OFF the hosting account before cancelling hosting.
        Start the API separately with ConnectionStrings__BookingDatabase pointing
        at the new database (see docs/local-portability.md).
        """);
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    if (values.Length % 2 != 0) throw new ArgumentException("Each option requires a value.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < values.Length; i += 2)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(values[i + 1]) ||
            !options.TryAdd(values[i], values[i + 1]))
        {
            throw new ArgumentException("Invalid or repeated argument: " + values[i]);
        }
    }
    return options;
}

static string Required(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing " + key);

try
{
    if (args.Length < 1 || args[0] is not ("backup" or "restore"))
    {
        Usage();
        return 2;
    }

    var options = ParseOptions(args[1..]);
    if (args[0] == "backup")
    {
        if (options.Count != 2) throw new ArgumentException("Backup takes --database and --directory only.");
        var output = await DatabasePortability.BackupAsync(
            Required(options, "--database"),
            Required(options, "--directory"));
        Console.WriteLine("Verified backup created: " + output);
        return 0;
    }

    if (options.Count != 2) throw new ArgumentException("Restore takes --from and --to only.");
    var destination = Required(options, "--to");
    await DatabasePortability.RestoreToNewAsync(Required(options, "--from"), destination);
    Console.WriteLine("Verified database restored to NEW file: " + Path.GetFullPath(destination));
    Console.WriteLine("The existing database was not changed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Database operation failed: " + error.Message);
    return 1;
}
