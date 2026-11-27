// ERP.DbMigrator — Console tool for applying database migrations.
// Usage: dotnet run --project tools/ERP.DbMigrator
//
// R2-DEV-03A — explicitly invoked, bounded maintenance (never run on login/refresh):
//   dotnet run --project tools/ERP.DbMigrator -- purge-consumed-refresh-tokens [--batch-size N] [--dry-run]
//
// Deletes at most N (default 1000, max 100000) consumed-refresh-token history rows whose
// RetainUntil (the session's absolute expiry) has passed, oldest first. Rows belonging to a
// session that can still be live are never deleted. Run repeatedly to drain a backlog.
// The Master DB connection and provider come from the same configuration as the design-time
// factory (ConnectionStrings__MasterDb, Database__Provider); nothing is printed about them.

using ERP.DbMigrator;
using ERP.Identity.Persistence;

const string PurgeCommand = "purge-consumed-refresh-tokens";

if (args.Length > 0 && args[0] == PurgeCommand)
{
    var batchSize = 1000;
    var dryRun = false;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dry-run":
                dryRun = true;
                break;
            case "--batch-size" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is >= 1 and <= 100_000:
                batchSize = n;
                i++;
                break;
            default:
                Console.Error.WriteLine($"Unknown or invalid argument: {args[i]}");
                Console.Error.WriteLine($"Usage: {PurgeCommand} [--batch-size 1..100000] [--dry-run]");
                return 3;
        }
    }

    await using var context = new MasterDbContextFactory().CreateDbContext([]);
    var sessions = new UserSessionRepository(context);
    var count = await sessions.PurgeExpiredConsumedAsync(DateTime.UtcNow, batchSize, dryRun);

    Console.WriteLine(dryRun
        ? $"Dry run: {count} expired consumed-refresh-token record(s) would be deleted (batch size {batchSize})."
        : $"Deleted {count} expired consumed-refresh-token record(s) (batch size {batchSize}).");
    return 0;
}

Console.WriteLine("ERP Database Migrator");
Console.WriteLine("=====================");
Console.WriteLine("This tool will be used to apply EF Core migrations to tenant databases.");
Console.WriteLine("Implementation will be added when we create the first migration.");
Console.WriteLine($"Maintenance: {PurgeCommand} [--batch-size N] [--dry-run]");
return 0;
