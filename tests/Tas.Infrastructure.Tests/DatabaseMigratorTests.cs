using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Tests;

/// <summary>Creating and upgrading databases from the SQL shipped in the build (uses scratch databases).</summary>
public sealed class DatabaseMigratorTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");

    private static async Task<string> FreshDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static DatabaseMigrator Migrator(string connectionString) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Tas"] = connectionString }).Build(),
            NullLogger<DatabaseMigrator>.Instance);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public void Migrations_ship_in_order_with_the_schema()
    {
        var versions = DatabaseMigrator.Migrations.Select(m => m.Version).ToList();
        Assert.NotEmpty(versions);
        Assert.Equal(versions.OrderBy(v => v).Distinct(), versions);
        Assert.Equal(Enumerable.Range(versions[0], versions.Count), versions); // no gaps
        Assert.True(DatabaseMigrator.Latest >= DatabaseMigrator.UnversionedBaseline);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS postgis", DatabaseMigrator.SchemaSql);
    }

    [Fact]
    public async Task A_new_database_is_created_and_later_migrations_are_applied()
    {
        if (ConnectionString is null)
            return;

        var db = await FreshDatabaseAsync("tas_migrate_test");
        var migrator = Migrator(db);

        var empty = await migrator.StatusAsync();
        Assert.Equal(0, empty.Current);
        Assert.Equal("schema", empty.Pending[0]);

        var done = await migrator.MigrateAsync();
        Assert.Contains("Created the database schema", Assert.Single(done));
        var status = await migrator.StatusAsync();
        Assert.True(status.UpToDate);
        Assert.Equal(DatabaseMigrator.Latest, status.Current);
        Assert.Equal((long)DatabaseMigrator.Migrations.Count, await ScalarAsync(db, "SELECT count(*) FROM schema_migration WHERE how = 'SCHEMA'"));

        Assert.Empty(await migrator.MigrateAsync()); // nothing to do

        // A database one release behind: the newest migration is applied, once. (Migrations are written to be
        // re-runnable — IF NOT EXISTS / IF EXISTS — so this works on the full schema too.)
        var last = DatabaseMigrator.Migrations[^1];
        await ScalarAsync(db, $"DELETE FROM schema_migration WHERE version = {last.Version}");
        Assert.Equal($"{last.Version:000}_{last.Name}", Assert.Single((await migrator.StatusAsync()).Pending));
        Assert.Equal($"Applied {last.Version:000}_{last.Name}.", Assert.Single(await migrator.MigrateAsync()));
        Assert.Equal("APPLIED", await ScalarAsync(db, $"SELECT how FROM schema_migration WHERE version = {last.Version}"));
        Assert.True((await migrator.StatusAsync()).UpToDate);
    }

    [Fact]
    public async Task A_database_made_by_hand_from_the_schema_is_taken_as_the_baseline()
    {
        if (ConnectionString is null)
            return;

        var db = await FreshDatabaseAsync("tas_baseline_test");
        await ScalarAsync(db, DatabaseMigrator.SchemaSql);

        var migrator = Migrator(db);
        var before = await migrator.StatusAsync();
        Assert.False(before.Versioned);
        Assert.Equal(DatabaseMigrator.UnversionedBaseline, before.Current);

        var done = await migrator.MigrateAsync();
        Assert.Contains($"recorded at version {DatabaseMigrator.UnversionedBaseline}", done[0]);
        Assert.True((await migrator.StatusAsync()).Versioned);
    }
}
