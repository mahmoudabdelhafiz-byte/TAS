using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tas.Infrastructure.Persistence;

/// <summary>
/// What the API does with migrations when it starts — <c>Database:Migrations</c>:
/// <list type="bullet">
/// <item><c>Apply</c> (default): bring the database to the latest version, then start.</item>
/// <item><c>Verify</c>: refuse to start unless the database is already up to date (for operators who run
///   <c>Tas.Api migrate</c> as a separate deployment step).</item>
/// <item><c>Off</c>: don't check.</item>
/// </list>
/// </summary>
public static class DatabaseStartup
{
    public static async Task RunAsync(IConfiguration config, ILoggerFactory loggers, CancellationToken ct = default)
    {
        var mode = config.GetValue("Database:Migrations", "Apply")!;
        if (Is(mode, "Off"))
            return;

        var migrator = Create(config, loggers);
        if (Is(mode, "Apply"))
        {
            await migrator.MigrateAsync(ct);
            return;
        }

        if (!Is(mode, "Verify"))
            throw new InvalidOperationException($"Unknown Database:Migrations value '{mode}'. Use Apply, Verify or Off.");

        var status = await migrator.StatusAsync(ct);
        if (!status.UpToDate)
        {
            var pending = status.Pending.Count == 0 ? "" : $" Pending: {string.Join(", ", status.Pending.Select(p => p.FileName))}.";
            throw new MigrationException(
                $"The database is at version {status.CurrentVersion}, this build needs {status.LatestVersion}.{pending} {string.Join(" ", status.Problems)} Run 'Tas.Api migrate' first.");
        }
    }

    /// <summary>
    /// <c>Tas.Api migrate</c> — apply pending migrations and exit.
    /// <c>Tas.Api migrate status</c> — show applied and pending migrations; exit code 1 if not up to date.
    /// </summary>
    public static async Task<int> CommandAsync(string[] args, IConfiguration config)
    {
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("migrate");
        try
        {
            var migrator = Create(config, loggers);
            if (args.Length > 0 && Is(args[0], "status"))
            {
                var status = await migrator.StatusAsync();
                Console.WriteLine($"Database version {status.CurrentVersion}, latest {status.LatestVersion}");
                foreach (var a in status.Applied)
                    Console.WriteLine($"  applied  {a.Version:D3}_{a.Name}  {a.AppliedAt:yyyy-MM-dd HH:mm}{(a.Baseline ? "  (baseline)" : "")}");
                foreach (var p in status.Pending)
                    Console.WriteLine($"  pending  {p.FileName}");
                foreach (var problem in status.Problems)
                    Console.WriteLine($"  PROBLEM  {problem}");
                return status.UpToDate ? 0 : 1;
            }

            var result = await migrator.MigrateAsync();
            Console.WriteLine(result.Applied.Count == 0
                ? $"Database is up to date (version {result.ToVersion})."
                : $"Migrated from version {result.FromVersion} to {result.ToVersion}: {string.Join(", ", result.Applied)}.");
            return 0;
        }
        catch (Exception ex) when (ex is MigrationException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            log.LogError("{Message}", ex.Message);
            return 1;
        }
    }

    private static DatabaseMigrator Create(IConfiguration config, ILoggerFactory loggers)
    {
        var connectionString = config.GetConnectionString("Tas")
                               ?? throw new InvalidOperationException("Connection string 'Tas' is not configured.");
        return new DatabaseMigrator(connectionString, loggers.CreateLogger<DatabaseMigrator>())
        {
            LockTimeout = TimeSpan.FromSeconds(config.GetValue("Database:MigrationLockTimeoutSeconds", 300)),
        };
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
