using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ofnr.Server.Data;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class TestHost : WebApplicationFactory<Program>
{
    public static string ServerConnection =>
        Environment.GetEnvironmentVariable("OFNR_TEST_DB") is { Length: > 0 } s ? s : "Host=127.0.0.1;Username=ofnr;Password=ofnr_dev";

    public string DataRoot { get; } = Directory.CreateTempSubdirectory("ofnr-").FullName;
    public string DbName { get; } = "ofnr_t_" + Guid.NewGuid().ToString("N")[..8];
    public string ConnectionString => new NpgsqlConnectionStringBuilder(ServerConnection) { Database = DbName }.ConnectionString;

    public TestHost()
    {
        ClientOptions.BaseAddress = new Uri("http://127.0.0.1:47821");
        // Keeps project folders inside the temp root even on a PC that has a D: drive.
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"),
            JsonSerializer.Serialize(new { projectsRoot = Path.Combine(DataRoot, "Projects") }));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("OFNR_DATA", DataRoot);
        builder.UseSetting("OFNR_DB", ConnectionString);
        builder.UseSetting("OFNR_PORT", "47821");
    }

    public HttpClient Authed()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        TestDb.Drop(DbName);
        try { Directory.Delete(DataRoot, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // git leaves read-only object files on Windows
    }
}

/// <summary>A unique empty database <c>ofnr_t_&lt;8 hex&gt;</c>; <see cref="Create"/> also migrates it. Dropped on dispose.</summary>
public sealed class TestDb : IDisposable
{
    public string DbName { get; } = "ofnr_t_" + Guid.NewGuid().ToString("N")[..8];
    public string ConnectionString => new NpgsqlConnectionStringBuilder(TestHost.ServerConnection) { Database = DbName }.ConnectionString;

    public static TestDb Create()
    {
        var t = new TestDb();
        using var db = t.Context();
        db.Database.Migrate();
        return t;
    }

    public OfnrDb Context() => new(new DbContextOptionsBuilder<OfnrDb>().UseNpgsql(ConnectionString).Options);

    public void Dispose() => Drop(DbName);

    public static void Drop(string dbName)
    {
        var admin = new NpgsqlConnectionStringBuilder(TestHost.ServerConnection) { Database = "postgres" }.ConnectionString;
        using var conn = new NpgsqlConnection(admin);
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", conn);
        cmd.ExecuteNonQuery();
    }
}
