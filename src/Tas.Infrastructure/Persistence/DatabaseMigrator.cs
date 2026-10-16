using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tas.Infrastructure.Persistence;

/// <param name="Current">Highest migration applied (0 = empty database).</param>
/// <param name="Latest">Highest migration this version of Yardsyncs ships.</param>
public sealed record DatabaseStatus(int Current, int Latest, IReadOnlyList<string> Pending, bool Versioned)
{
    public bool UpToDate => Pending.Count == 0;
}

/// <summary>
/// Creates and upgrades the database from the SQL that ships inside the application (db/schema.sql and
/// db/migrations/NNN_name.sql), so a terminal on-premise and the cloud service upgrade the same way.
///
/// - Empty database: runs schema.sql (the whole current schema) and records every migration as included.
/// - Versioned database: runs the migrations it hasn't had yet, each in its own transaction, in order.
/// - A database created by hand from schema.sql before versioning existed (no schema_migration table but tables
///   present) is taken to be at <see cref="UnversionedBaseline"/>.
/// A PostgreSQL advisory lock keeps two app instances from migrating at the same time.
/// </summary>
public sealed partial class DatabaseMigrator(IConfiguration config, ILogger<DatabaseMigrator> logger)
{
    /// <summary>Version of db/schema.sql when the migration history started (2026-10-16).</summary>
    public const int UnversionedBaseline = 11;

    private const long LockKey = 7_316_501_202_610_16; // any constant shared by all instances

    public sealed record Migration(int Version, string Name, string Sql)
    {
        public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
    }

    [GeneratedRegex(@"^db/migrations/(\d{3})_([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex MigrationName();

    /// <summary>The migrations shipped in this build, in order.</summary>
    public static IReadOnlyList<Migration> Migrations { get; } = Load();

    public static string SchemaSql { get; } = Read("db/schema.sql");

    private static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Select(n => (Name: n, Match: MigrationName().Match(n)))
            .Where(x => x.Match.Success)
            .Select(x => new Migration(int.Parse(x.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), x.Match.Groups[2].Value, Read(x.Name)))
            .OrderBy(m => m.Version)
            .ToList();
    }

    private static string Read(string resource)
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Embedded resource {resource} is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static int Latest => Migrations.Count == 0 ? 0 : Migrations[^1].Version;

    private string ConnectionString => config.GetConnectionString("Tas")
        ?? throw new InvalidOperationException("Connection string 'Tas' is not configured.");

    public async Task<DatabaseStatus> StatusAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var versioned = await TableExistsAsync(connection, "schema_migration", ct);
        var current = versioned
            ? await ScalarAsync<int?>(connection, "SELECT max(version) FROM schema_migration", ct) ?? 0
            : await TableExistsAsync(connection, "terminal", ct) ? UnversionedBaseline : 0;
        var pending = Migrations.Where(m => m.Version > current).Select(m => $"{m.Version:000}_{m.Name}").ToList();
        if (current == 0)
            pending.Insert(0, "schema");
        return new DatabaseStatus(current, Latest, pending, versioned);
    }

    /// <summary>Brings the database to the latest version. Returns what was done, for the log.</summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        var done = new List<string>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS schema_migration (
                    version     integer      PRIMARY KEY,
                    name        varchar(100) NOT NULL,
                    checksum    char(64)     NOT NULL,
                    how         varchar(10)  NOT NULL CHECK (how IN ('SCHEMA','BASELINE','APPLIED')),
                    applied_at  timestamptz  NOT NULL DEFAULT now()
                )
                """, ct);

            var applied = await AppliedAsync(connection, ct);
            if (applied.Count == 0)
            {
                if (!await TableExistsAsync(connection, "terminal", ct))
                {
                    // A new database: the whole schema at once, every migration counted as included.
                    await using var tx = await connection.BeginTransactionAsync(ct);
                    await ExecuteAsync(connection, SchemaSql, ct, tx);
                    foreach (var m in Migrations)
                        await RecordAsync(connection, m, "SCHEMA", ct, tx);
                    await tx.CommitAsync(ct);
                    done.Add($"Created the database schema (version {Latest}).");
                    logger.LogInformation("Database created at version {Version}", Latest);
                    return done;
                }

                // Made from schema.sql by hand before versioning: at the baseline.
                foreach (var m in Migrations.Where(m => m.Version <= UnversionedBaseline))
                    await RecordAsync(connection, m, "BASELINE", ct);
                done.Add($"Existing database recorded at version {UnversionedBaseline}.");
                logger.LogWarning("Database without migration history taken to be at version {Version}", UnversionedBaseline);
                applied = await AppliedAsync(connection, ct);
            }

            foreach (var (version, checksum) in applied)
            {
                var shipped = Migrations.FirstOrDefault(m => m.Version == version);
                if (shipped is not null && shipped.Checksum != checksum)
                    logger.LogWarning("Migration {Version}_{Name} changed after it was applied", version, shipped.Name);
            }

            foreach (var m in Migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using var tx = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, m.Sql, ct, tx);
                await RecordAsync(connection, m, "APPLIED", ct, tx);
                await tx.CommitAsync(ct);
                done.Add($"Applied {m.Version:000}_{m.Name}.");
                logger.LogInformation("Applied migration {Version}_{Name}", m.Version, m.Name);
            }
            return done;
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static async Task<Dictionary<int, string>> AppliedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var applied = new Dictionary<int, string>();
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migration", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            applied[reader.GetInt32(0)] = reader.GetString(1);
        return applied;
    }

    private static async Task RecordAsync(NpgsqlConnection connection, Migration m, string how, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO schema_migration (version, name, checksum, how) VALUES (@v, @n, @c, @h) ON CONFLICT (version) DO NOTHING", connection, tx);
        cmd.Parameters.AddWithValue("v", m.Version);
        cmd.Parameters.AddWithValue("n", m.Name);
        cmd.Parameters.AddWithValue("c", m.Checksum);
        cmd.Parameters.AddWithValue("h", how);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table, CancellationToken ct) =>
        await ScalarAsync<bool>(connection, $"SELECT to_regclass('public.{table}') IS NOT NULL", ct);

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
