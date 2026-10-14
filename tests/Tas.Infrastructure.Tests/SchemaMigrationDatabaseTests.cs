using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Tests;

/// <summary>
/// The migration runner against real, throwaway databases on the TAS_TEST_DB server — and the check that
/// db/migrations rebuilds exactly the schema in db/schema.sql.
/// </summary>
public sealed class SchemaMigrationDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private readonly List<string> _databases = [];

    public Task InitializeAsync()
    {
        if (ConnectionString is null && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var name in _databases)
            await AdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    private static async Task AdminAsync(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A new empty database; returns its connection string.</summary>
    private async Task<string> NewDatabaseAsync()
    {
        var name = $"tas_mig_{Guid.NewGuid():N}"[..24];
        await AdminAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static DatabaseMigrator Migrator(string connectionString, IReadOnlyList<MigrationScript>? scripts = null) =>
        new(connectionString, NullLogger.Instance, scripts);

    private static IReadOnlyList<MigrationScript> Scripts => DatabaseMigrator.EmbeddedScripts();

    /// <summary>Tables, columns, constraints, indexes and extensions — everything but the runner's own history table.</summary>
    private static async Task<List<string>> CatalogAsync(string connectionString)
    {
        const string sql =
            """
            SELECT 'col ' || table_name || '.' || column_name || ' ' || data_type || ' ' || coalesce(character_maximum_length::text, '')
                   || ' ' || is_nullable || ' ' || coalesce(column_default, '') || ' ' || coalesce(is_identity, '')
              FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> 'schema_migration'
            UNION ALL
            SELECT 'con ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
              FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND conrelid::regclass::text <> 'schema_migration'
            UNION ALL
            SELECT 'idx ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename <> 'schema_migration'
            UNION ALL
            SELECT 'ext ' || extname FROM pg_extension
            ORDER BY 1
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public async Task Migrations_rebuild_exactly_the_schema_snapshot()
    {
        if (ConnectionString is null) return;

        var fromSnapshot = await NewDatabaseAsync();
        await ExecAsync(fromSnapshot, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema.sql")));

        var fromMigrations = await NewDatabaseAsync();
        var result = await Migrator(fromMigrations).MigrateAsync();
        Assert.Equal(Scripts.Count, result.Applied.Count);

        var expected = await CatalogAsync(fromSnapshot);
        var actual = await CatalogAsync(fromMigrations);
        Assert.True(expected.Count > 500);

        // On failure this lists exactly which table / column / constraint / index differs.
        Assert.Equal(expected.Except(actual).ToList(), actual.Except(expected).ToList());
        Assert.Empty(expected.Except(actual));
    }

    [Fact]
    public async Task An_empty_database_is_migrated_once_and_then_reported_up_to_date()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();

        Assert.Equal(0, (await Migrator(db).StatusAsync()).CurrentVersion);
        var first = await Migrator(db).MigrateAsync();
        Assert.Equal((0, Scripts.Count, (int?)null), (first.FromVersion, first.ToVersion, first.BaselinedTo));
        Assert.Equal(Scripts.Select(s => s.FileName), first.Applied);

        var second = await Migrator(db).MigrateAsync();
        Assert.Empty(second.Applied);

        var status = await Migrator(db).StatusAsync();
        Assert.True(status.UpToDate);
        Assert.All(status.Applied, a => Assert.False(a.Baseline));
        Assert.Equal(Scripts.Select(s => s.Checksum), status.Applied.Select(a => a.Checksum));
    }

    [Fact]
    public async Task A_database_created_from_the_snapshot_before_the_runner_existed_is_baselined()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();
        await ExecAsync(db, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema.sql")));
        await ExecAsync(db, "INSERT INTO terminal (code, name, terminal_type) VALUES ('KEEP', 'Existing data', 'CONTAINER')");

        var status = await Migrator(db).StatusAsync();
        Assert.Equal(6, status.CurrentVersion);
        Assert.Contains("baseline", Assert.Single(status.Problems));

        var result = await Migrator(db).MigrateAsync();
        Assert.Equal(6, result.BaselinedTo);
        Assert.Equal(Scripts.Skip(6).Select(s => s.FileName), result.Applied); // only what came after the runner
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT count(*) FROM terminal WHERE code = 'KEEP'"));

        var after = await Migrator(db).StatusAsync();
        Assert.True(after.UpToDate);
        Assert.All(after.Applied.Where(a => a.Version <= 6), a => Assert.True(a.Baseline));
    }

    [Fact]
    public async Task An_older_database_upgraded_by_hand_is_recognised_and_the_rest_is_applied()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();
        foreach (var script in Scripts.Take(3)) // v0.2 + auth + revalidation, the way a customer DB would look
            await ExecAsync(db, script.Sql);

        var result = await Migrator(db).MigrateAsync();
        Assert.Equal(3, result.BaselinedTo);
        Assert.Equal(Scripts.Skip(3).Select(s => s.FileName), result.Applied);
        Assert.True((await Migrator(db).StatusAsync()).UpToDate);
    }

    [Fact]
    public async Task Editing_an_applied_migration_or_running_an_older_build_is_refused()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();
        await Migrator(db).MigrateAsync();

        var edited = Scripts.Select(s => s.Version == 2 ? s with { Sql = s.Sql + "\n-- tweaked" } : s).ToList();
        var changed = await Assert.ThrowsAsync<MigrationException>(() => Migrator(db, edited).MigrateAsync());
        Assert.Contains("002_auth.sql was changed after it was applied", changed.Message);
        Assert.False((await Migrator(db, edited).StatusAsync()).UpToDate);

        var olderBuild = Scripts.Take(Scripts.Count - 1).ToList();
        var newer = await Assert.ThrowsAsync<MigrationException>(() => Migrator(db, olderBuild).MigrateAsync());
        Assert.Contains("upgraded by a newer version", newer.Message);
    }

    [Fact]
    public async Task A_failing_migration_is_rolled_back_and_leaves_the_previous_version()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();
        var next = Scripts.Count + 1;
        var broken = Scripts
            .Append(new MigrationScript(next, "add_column", "ALTER TABLE terminal ADD COLUMN half_done integer;"))
            .Append(new MigrationScript(next + 1, "broken", "ALTER TABLE terminal ADD COLUMN also_half integer;\nALTER TABLE no_such_table ADD COLUMN x integer;"))
            .ToList();

        var error = await Assert.ThrowsAsync<MigrationException>(() => Migrator(db, broken).MigrateAsync());
        Assert.Contains($"{next + 1:D3}_broken.sql failed and was rolled back", error.Message);
        Assert.Contains($"stays at version {next}", error.Message);

        Assert.Equal(next, (await Migrator(db, broken).StatusAsync()).CurrentVersion);
        Assert.True(await ScalarAsync<bool>(db, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'terminal' AND column_name = 'half_done')"));
        Assert.False(await ScalarAsync<bool>(db, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'terminal' AND column_name = 'also_half')"));
    }

    [Fact]
    public async Task Several_instances_starting_at_once_migrate_the_database_exactly_once()
    {
        if (ConnectionString is null) return;
        var db = await NewDatabaseAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Migrator(db).MigrateAsync()));

        Assert.Equal(Scripts.Count, results.Sum(r => r.Applied.Count));
        Assert.Single(results, r => r.Applied.Count > 0);
        Assert.Equal((long)Scripts.Count, await ScalarAsync<long>(db, "SELECT count(*) FROM schema_migration"));
    }
}
