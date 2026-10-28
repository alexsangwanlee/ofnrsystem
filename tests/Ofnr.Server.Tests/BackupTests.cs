using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ofnr.Server.Mail;
using Ofnr.Server.Ops;
using Ofnr.Server.Security;

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

    /// <summary>The db password setup.ps1 saved with DPAPI, which this OS does not have.</summary>
    sealed class FakeSecrets(SettingsStore settings) : SecretStore(settings, new ConfigurationBuilder().Build())
    {
        public override string? Load(string name) => name == "db" ? "s3cret-from-setup" : null;
    }

    [Fact]
    public async Task Db_password_comes_from_secret_store_and_reaches_pg_dump_only_through_PGPASSWORD()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake pg_dump is a shell script.");
        var settings = new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", data)]).Build());
        var secrets = new FakeSecrets(settings);
        const string noPassword = "Host=127.0.0.1;Port=5432;Database=ofnr_ops;Username=ofnr"; // what setup.ps1 writes
        var pgDump = settings.Current.PgDumpPath = Path.Combine(data, "pg_dump");
        settings.Current.ConnectionString = noPassword;
        File.WriteAllText(pgDump, "#!/bin/sh\nprintf '%s\\n' \"$PGPASSWORD\" \"$LC_MESSAGES\" \"$@\" > \"$0.log\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pgDump, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(data, "out.dump");

        await new PgDumpRunner(settings, secrets).DumpAsync(file, Ct);

        Assert.Equal(["s3cret-from-setup", "C", "-Fc", "-w", "-f", file, "-h", "127.0.0.1", "-p", "5432", "-U", "ofnr", "ofnr_ops"], File.ReadAllLines(pgDump + ".log"));
        Assert.Equal(noPassword + ";Password=s3cret-from-setup", secrets.WithDbPassword(noPassword));
        Assert.Equal(noPassword + ";Password=ofnr_dev", secrets.WithDbPassword(noPassword + ";Password=ofnr_dev"));
    }

    /// <summary>A database one release behind: it has an applied migration, and Initial is pending.</summary>
    static void OlderDatabase(TestHost host)
    {
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
    }

    [Fact]
    public async Task Pending_migration_triggers_backup_first()
    {
        await using var host = new TestHost();
        OlderDatabase(host);
        var seen = new List<string[]>();
        var dump = new FakeDump(async () => seen.Add(await AppliedAsync(host.ConnectionString)));

        var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IDumpRunner>(dump)));
        _ = app.Services; // starts the server

        Assert.Equal(["20260101000000_Older"], Assert.Single(seen));
        Assert.Contains("20261027154244_Initial", await AppliedAsync(host.ConnectionString));
        Assert.StartsWith(Path.Combine(host.DataRoot, "backups", "ofnr-"), Assert.Single(dump.Files));
    }

    [Fact]
    public async Task Failed_backup_before_migration_names_the_pg_dump_error_in_db_health()
    {
        await using var host = new TestHost();
        OlderDatabase(host);
        var dump = new FakeDump(() => throw new InvalidOperationException("pg_dump: 연결 실패"));
        var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IDumpRunner>(dump)));
        var client = app.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:47821") });
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);

        var db = (await client.GetFromJsonAsync<JsonElement>("/api/health", Ct)).GetProperty("db");

        Assert.Equal((false, "업데이트 전에 데이터베이스를 백업하지 못해 업데이트를 적용하지 않았습니다. 설정에서 pg_dump 경로를 확인하고 앱을 다시 시작해 주세요: pg_dump: 연결 실패"),
            (db.GetProperty("ok").GetBoolean(), db.GetProperty("detail").GetString()));
        Assert.Equal(["20260101000000_Older"], await AppliedAsync(host.ConnectionString));
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
