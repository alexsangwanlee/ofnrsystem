using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Ofnr.Server.Tests;

public sealed class SettingsStoreTests
{
    readonly string data = Directory.CreateTempSubdirectory("ofnr-s-").FullName;

    string SettingsPath => Path.Combine(data, "settings.json");

    SettingsStore Create(string? json, params KeyValuePair<string, string?>[] env)
    {
        if (json is not null) File.WriteAllText(SettingsPath, json);
        return new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", data), .. env]).Build());
    }

    JsonElement Saved() => JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement;

    [Fact]
    public void First_run_writes_settings_so_projects_root_stays_fixed()
    {
        var store = Create(null);
        Assert.Equal(store.Current.ProjectsRoot, Saved().GetProperty("projectsRoot").GetString());
        Assert.False(Saved().TryGetProperty("connectionString", out _)); // other defaults stay free to change in later releases
    }

    [Fact]
    public void Hand_edited_json_with_comment_and_trailing_comma_loads()
    {
        var store = Create("{\n  // 포트\n  \"port\": 47900,\n}");
        Assert.Equal(47900, store.Current.Port);
    }

    [Fact]
    public void Broken_json_names_the_file_in_korean()
    {
        var e = Assert.Throws<InvalidDataException>(() => Create("{ \"port\": "));
        Assert.Equal($"settings.json 형식이 잘못되었습니다: {SettingsPath}", e.Message);
    }

    [Fact]
    public async Task Save_keeps_file_values_where_env_overrides()
    {
        var store = Create("{ \"port\": 47900, \"connectionString\": \"Host=file\" }",
            new("OFNR_PORT", "40000"), new("OFNR_DB", "Host=env"));
        store.Current.MaxConcurrentJobs = 3;

        await store.SaveAsync(store.Current);

        Assert.Equal(47900, Saved().GetProperty("port").GetInt32());
        Assert.Equal("Host=file", Saved().GetProperty("connectionString").GetString());
        Assert.Equal(3, Saved().GetProperty("maxConcurrentJobs").GetInt32());
        Assert.Equal(40000, store.Current.Port);
        Assert.Equal("Host=env", store.Current.ConnectionString);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_collide()
    {
        var store = Create("{}");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.SaveAsync(store.Current))));
        Assert.Equal(store.Current.Port, Saved().GetProperty("port").GetInt32());
    }
}
