using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Tests;

/// <summary>The migration files themselves (no database needed).</summary>
public class MigrationScriptTests
{
    [Fact]
    public void Every_file_in_db_migrations_is_embedded_numbered_and_in_order()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "db", "migrations"), "*.sql")
            .Select(Path.GetFileName).Order().ToList();
        var embedded = DatabaseMigrator.EmbeddedScripts();

        Assert.True(files.Count >= 6);
        Assert.Equal(files, embedded.Select(s => s.FileName).ToList());
        Assert.Equal(Enumerable.Range(1, files.Count), embedded.Select(s => s.Version));
        Assert.Equal("initial", embedded[0].Name);
        Assert.All(embedded, s => Assert.False(string.IsNullOrWhiteSpace(s.Sql)));
    }

    [Fact]
    public void Checksums_ignore_line_endings_and_a_bom_but_not_content()
    {
        var unix = new MigrationScript(1, "x", "CREATE TABLE a (id int);\nCREATE TABLE b (id int);\n");
        var windows = new MigrationScript(1, "x", "﻿CREATE TABLE a (id int);\r\nCREATE TABLE b (id int);\r\n");
        var edited = new MigrationScript(1, "x", "CREATE TABLE a (id bigint);\nCREATE TABLE b (id int);\n");

        Assert.Equal(unix.Checksum, windows.Checksum);
        Assert.NotEqual(unix.Checksum, edited.Checksum);
        Assert.Matches("^[0-9a-f]{64}$", unix.Checksum);
    }

    [Theory]
    [InlineData("7_gate.sql")]
    [InlineData("007-gate.sql")]
    [InlineData("007_Gate.sql")]
    [InlineData("007_gate.txt")]
    public void Badly_named_files_are_rejected(string name) =>
        Assert.Throws<MigrationException>(() => DatabaseMigrator.Parse(name, "SELECT 1"));

    [Fact]
    public void Gaps_and_duplicates_are_rejected()
    {
        MigrationScript S(int v) => new(v, $"m{v}", "SELECT 1");
        Assert.Equal([1, 2, 3], DatabaseMigrator.Validate([S(3), S(1), S(2)]).Select(s => s.Version));
        Assert.Throws<MigrationException>(() => DatabaseMigrator.Validate([S(1), S(3)]));
        Assert.Throws<MigrationException>(() => DatabaseMigrator.Validate([S(1), S(2), S(2)]));
        Assert.Throws<MigrationException>(() => DatabaseMigrator.Validate([S(2)]));
        Assert.Throws<MigrationException>(() => DatabaseMigrator.Validate([]));
    }
}
