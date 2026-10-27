using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
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
        var admin = new NpgsqlConnectionStringBuilder(ServerConnection) { Database = "postgres" }.ConnectionString;
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DbName}\" WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        try { Directory.Delete(DataRoot, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // git leaves read-only object files on Windows
    }
}
