using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tas.Infrastructure.Persistence;

/// <summary>One numbered SQL file from db/migrations, e.g. 004_gate.sql.</summary>
public sealed record MigrationScript(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script with line endings normalized, so a Windows checkout gives the same value.</summary>
    public string Checksum => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Sql.TrimStart('﻿').Replace("\r\n", "\n"))));

    public string FileName => $"{Version:D3}_{Name}.sql";
}

public sealed record AppliedMigration(int Version, string Name, string Checksum, DateTimeOffset AppliedAt, int ExecutionMs, bool Baseline);

/// <param name="Problems">Reasons the database can't be migrated safely; empty when fine.</param>
public sealed record MigrationStatus(
    int CurrentVersion,
    int LatestVersion,
    IReadOnlyList<AppliedMigration> Applied,
    IReadOnlyList<MigrationScript> Pending,
    IReadOnlyList<string> Problems)
{
    public bool UpToDate => Pending.Count == 0 && Problems.Count == 0;
}

/// <param name="BaselinedTo">Set when an existing database without migration history was recognised at this version.</param>
public sealed record MigrationRunResult(int FromVersion, int ToVersion, IReadOnlyList<string> Applied, int? BaselinedTo);

public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Creates and upgrades the database from the numbered scripts in db/migrations (embedded in this assembly).
///
/// - Applied versions are recorded in <c>schema_migration</c> with a checksum; an applied script that was edited
///   afterwards stops the run (fix: put the change in a new script).
/// - Each script runs in its own transaction: a failing script leaves the database at the previous version.
/// - A PostgreSQL advisory lock makes concurrent runs (several API instances starting together) wait for each other.
/// - Databases created before this runner existed (from db/schema.sql or the upgrade scripts by hand) have no
///   history; their version is recognised from the schema and recorded as a baseline, then newer scripts apply.
/// </summary>
public sealed class DatabaseMigrator(string connectionString, ILogger logger, IReadOnlyList<MigrationScript>? scripts = null)
{
    private const long LockKey = 7_340_100;
    private static readonly Regex FileNamePattern = new(@"^(\d{3})_([a-z0-9_]+)\.sql$", RegexOptions.Compiled);

    private readonly IReadOnlyList<MigrationScript> _scripts = Validate(scripts ?? EmbeddedScripts());

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public IReadOnlyList<MigrationScript> Scripts => _scripts;

    /// <summary>The scripts compiled into this build (db/migrations/*.sql).</summary>
    public static IReadOnlyList<MigrationScript> EmbeddedScripts()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        const string prefix = "Tas.Migrations.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => Parse(n[prefix.Length..], ReadResource(assembly, n)))
            .OrderBy(s => s.Version)
            .ToList();
    }

    public static MigrationScript Parse(string fileName, string sql)
    {
        var match = FileNamePattern.Match(fileName);
        if (!match.Success)
            throw new MigrationException($"Migration file '{fileName}' must be named NNN_lower_case_name.sql.");
        return new MigrationScript(int.Parse(match.Groups[1].Value), match.Groups[2].Value, sql);
    }

    /// <summary>Versions must start at 1 and have no gaps or duplicates.</summary>
    public static IReadOnlyList<MigrationScript> Validate(IReadOnlyList<MigrationScript> scripts)
    {
        var ordered = scripts.OrderBy(s => s.Version).ToList();
        if (ordered.Count == 0)
            throw new MigrationException("No migration scripts found.");
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
                throw new MigrationException(
                    $"Migration versions must be 1, 2, 3… without gaps or duplicates; found {ordered[i].FileName} where version {i + 1} was expected.");
        }
        return ordered;
    }

    public async Task<MigrationStatus> StatusAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var applied = await ReadHistoryAsync(connection, ct);
        if (applied is null)
        {
            var detected = await DetectLegacyVersionAsync(connection, null, ct);
            var note = detected > 0
                ? [$"No migration history; the schema looks like version {detected}. It will be recorded as a baseline on the next migrate."]
                : Array.Empty<string>();
            return new MigrationStatus(detected, Latest, [], _scripts.Where(s => s.Version > detected).ToList(), note);
        }

        return BuildStatus(applied);
    }

    /// <summary>Brings the database to the latest version. Throws <see cref="MigrationException"/> if it can't safely.</summary>
    public async Task<MigrationRunResult> MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await AcquireLockAsync(connection, ct);
        try
        {
            int? baselined = null;
            var applied = await ReadHistoryAsync(connection, ct);
            if (applied is null)
            {
                // Creating the history and recording a baseline happen together, so a crash can't leave an
                // empty history in front of an existing schema.
                await using var tx = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, tx, HistoryTableSql, ct);
                var legacy = await DetectLegacyVersionAsync(connection, tx, ct);
                foreach (var script in _scripts.Where(s => s.Version <= legacy))
                    await RecordAsync(connection, tx, script, 0, baseline: true, ct);
                await tx.CommitAsync(ct);

                if (legacy > 0)
                {
                    baselined = legacy;
                    logger.LogWarning(
                        "Database had no migration history; recognised the schema as version {Version} ({File}) and recorded it as a baseline",
                        legacy, _scripts[legacy - 1].FileName);
                }
                applied = await ReadHistoryAsync(connection, ct) ?? [];
            }

            var status = BuildStatus(applied);
            if (status.Problems.Count > 0)
                throw new MigrationException(string.Join(" ", status.Problems));

            var done = new List<string>();
            foreach (var script in status.Pending)
            {
                ct.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                await using var tx = await connection.BeginTransactionAsync(ct);
                try
                {
                    await ExecuteAsync(connection, tx, script.Sql, ct);
                    await RecordAsync(connection, tx, script, (int)watch.ElapsedMilliseconds, baseline: false, ct);
                    await tx.CommitAsync(ct);
                }
                catch (Exception ex) when (ex is PostgresException or NpgsqlException)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    throw new MigrationException(
                        $"Migration {script.FileName} failed and was rolled back; the database stays at version {status.CurrentVersion + done.Count}. {ex.Message}", ex);
                }

                done.Add(script.FileName);
                logger.LogInformation("Applied migration {File} in {Ms} ms", script.FileName, watch.ElapsedMilliseconds);
            }

            var to = status.CurrentVersion + done.Count;
            if (done.Count == 0)
                logger.LogInformation("Database is up to date (version {Version})", to);
            return new MigrationRunResult(status.CurrentVersion, to, done, baselined);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockKey})", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private int Latest => _scripts[^1].Version;

    private MigrationStatus BuildStatus(IReadOnlyList<AppliedMigration> applied)
    {
        var problems = new List<string>();
        var byVersion = _scripts.ToDictionary(s => s.Version);

        foreach (var a in applied)
        {
            if (!byVersion.TryGetValue(a.Version, out var script))
                problems.Add($"The database has migration {a.Version:D3}_{a.Name} which this build doesn't know: it was upgraded by a newer version of TAS. Deploy that version (or newer).");
            else if (!a.Baseline && !string.Equals(a.Checksum, script.Checksum, StringComparison.Ordinal))
                problems.Add($"Migration {script.FileName} was changed after it was applied to this database. Undo the edit and put the change in a new migration.");
        }

        var appliedVersions = applied.Select(a => a.Version).ToHashSet();
        var current = applied.Count == 0 ? 0 : applied.Max(a => a.Version);
        var pending = _scripts.Where(s => !appliedVersions.Contains(s.Version)).ToList();
        var outOfOrder = pending.Where(s => s.Version < current).ToList();
        if (outOfOrder.Count > 0)
            problems.Add($"Migrations {string.Join(", ", outOfOrder.Select(s => s.FileName))} are older than the database version {current} but were never applied.");

        return new MigrationStatus(current, Latest, applied, pending, problems);
    }

    // ------------------------------------------------------------------ history table

    private const string HistoryTableSql =
        """
        CREATE TABLE IF NOT EXISTS schema_migration (   -- applied db/migrations scripts (written by the migration runner)
            version       integer      PRIMARY KEY,
            name          varchar(200) NOT NULL,
            checksum      char(64)     NOT NULL,
            applied_at    timestamptz  NOT NULL DEFAULT now(),
            execution_ms  integer      NOT NULL,
            baseline      boolean      NOT NULL DEFAULT false   -- recorded for a database that existed before the runner
        );
        """;

    /// <summary>Null when the history table doesn't exist yet.</summary>
    private static async Task<IReadOnlyList<AppliedMigration>?> ReadHistoryAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('schema_migration') IS NOT NULL", connection))
        {
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
                return null;
        }

        var list = new List<AppliedMigration>();
        await using var command = new NpgsqlCommand(
            "SELECT version, name, checksum, applied_at, execution_ms, baseline FROM schema_migration ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AppliedMigration(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2).Trim(),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.GetBoolean(5)));
        }
        return list;
    }

    private static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, MigrationScript script, int ms, bool baseline, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO schema_migration (version, name, checksum, execution_ms, baseline) VALUES (@v, @n, @c, @ms, @b)", connection, tx);
        command.Parameters.AddWithValue("v", script.Version);
        command.Parameters.AddWithValue("n", script.Name);
        command.Parameters.AddWithValue("c", script.Checksum);
        command.Parameters.AddWithValue("ms", ms);
        command.Parameters.AddWithValue("b", baseline);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ legacy databases

    /// <summary>
    /// Version of a database created before the runner existed, recognised by what each script added.
    /// 0 = empty database. Only covers versions 1–6: every later database has a history table, so never extend this.
    /// </summary>
    private static async Task<int> DetectLegacyVersionAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken ct)
    {
        (int Version, string Sql)[] fingerprints =
        [
            (6, "SELECT to_regclass('password_token') IS NOT NULL"),
            (5, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'notification_log' AND column_name = 'attempts')"),
            (4, "SELECT to_regclass('gate_trouble') IS NOT NULL"),
            (3, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'appointment' AND column_name = 'last_validated_at')"),
            (2, "SELECT to_regclass('refresh_token') IS NOT NULL"),
            (1, "SELECT to_regclass('terminal') IS NOT NULL"),
        ];

        foreach (var (version, sql) in fingerprints)
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            if ((bool)(await command.ExecuteScalarAsync(ct))!)
                return version;
        }
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        var waiting = false;
        while (true)
        {
            await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", connection);
            if ((bool)(await command.ExecuteScalarAsync(ct))!)
                return;
            if (!waiting)
            {
                logger.LogInformation("Another process is migrating the database; waiting for it to finish");
                waiting = true;
            }
            if (DateTime.UtcNow > deadline)
                throw new MigrationException($"Timed out after {LockTimeout.TotalSeconds:0} s waiting for another process migrating the database.");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
