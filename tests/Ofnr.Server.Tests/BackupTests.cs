using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Tests;

public sealed class BackupTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string data = Directory.CreateTempSubdirectory("ofnr-bk-").FullName;

    /// <summary>Writes a small file where pg_dump would, after <paramref name="before"/> has looked at the database.</summary>
    sealed class FakeDump(Func<Task>? before = null) : IDumpRunner
    {
        public List<string> Files { get; } = [];

        public async Task DumpAsync(string file, CancellationToken ct)
        {
            if (before is not null) await before();
            Files.Add(file);
            await File.WriteAllTextAsync(file, "dump", ct);
        }
    }

    (Backup Backup, string Dir) Create(FakeDump dump)
    {
        var settings = new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", data)]).Build());
        Directory.CreateDirectory(settings.Current.BackupsDir);
        return (new Backup(settings, dump, NullLogger<Backup>.Instance), settings.Current.BackupsDir);
    }

    static void Old(string dir, string name, TimeSpan age)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

    [Fact]
    public async Task Backup_keeps_fourteen_newest()
    {
        var dump = new FakeDump();
        var (backup, dir) = Create(dump);
        for (var i = 1; i <= 16; i++) Old(dir, $"ofnr-old{i:00}.dump", TimeSpan.FromDays(i + 1));
        Old(dir, "notes.txt", TimeSpan.FromDays(30));

        Assert.True(await backup.RunIfDueAsync(Ct));

        var made = Path.GetFileName(Assert.Single(dump.Files));
        Assert.Matches(@"^ofnr-\d{8}-\d{4}\.dump$", made);
        Assert.Equal([made, .. Enumerable.Range(1, 13).Select(i => $"ofnr-old{i:00}.dump")],
            Directory.GetFiles(dir, "ofnr-*.dump").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
    }

    [Fact]
    public async Task Backup_skips_when_recent()
    {
        var dump = new FakeDump();
        var (backup, dir) = Create(dump);
        Old(dir, "ofnr-20261027-0300.dump", TimeSpan.FromHours(23));

        Assert.False(await backup.RunIfDueAsync(Ct));
        Assert.Empty(dump.Files);
    }

    [Fact]
    public async Task Failed_dump_leaves_no_file_behind()
    {
        var (backup, dir) = Create(new FakeDump(() => throw new InvalidOperationException("pg_dump: 연결 실패")));
        Old(dir, "ofnr-20261020-0300.dump", TimeSpan.FromDays(7));

        await Assert.ThrowsAsync<InvalidOperationException>(() => backup.RunIfDueAsync(Ct));

        Assert.Equal(["ofnr-20261020-0300.dump"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.False(backup.Health.Ok);
        Assert.Contains("pg_dump: 연결 실패", backup.Health.Detail);
    }

    [Fact]
    public async Task Pending_migration_triggers_backup_first()
    {
        await using var host = new TestHost();
        // A database one release behind: it has an applied migration, and Initial is pending.
        using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(TestHost.ServerConnection) { Database = "postgres" }.ConnectionString))
        {
            admin.Open();
            using var create = new NpgsqlCommand($"CREATE DATABASE \"{host.DbName}\"", admin);
            create.ExecuteNonQuery();
        }
        using (var conn = new NpgsqlConnection(host.ConnectionString))
        {
            conn.Open();
            using var older = new NpgsqlCommand("""
                CREATE TABLE "__EFMigrationsHistory" (migration_id varchar(150) PRIMARY KEY, product_version varchar(32) NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('20260101000000_Older', '10.0.14');
                """, conn);
            older.ExecuteNonQuery();
        }
        var seen = new List<string[]>();
        var dump = new FakeDump(async () => seen.Add(await AppliedAsync(host.ConnectionString)));

        var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IDumpRunner>(dump)));
        _ = app.Services; // starts the server

        Assert.Equal(["20260101000000_Older"], Assert.Single(seen));
        Assert.Contains("20261027154244_Initial", await AppliedAsync(host.ConnectionString));
        Assert.StartsWith(Path.Combine(host.DataRoot, "backups", "ofnr-"), Assert.Single(dump.Files));
    }

    static async Task<string[]> AppliedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("""SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY 1""", conn);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<string>();
        while (await r.ReadAsync(Ct)) ids.Add(r.GetString(0));
        return [.. ids];
    }
}
