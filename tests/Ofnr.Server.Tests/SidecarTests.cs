using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;

namespace Ofnr.Server.Tests;

/// <summary>The built server as Tauri runs it: <c>--sidecar</c>, pipes for stdin and stdout.</summary>
public sealed class SidecarTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sidecar on the host's data root and database (its own in-memory server never starts).</summary>
    sealed class Sidecar : IDisposable
    {
        readonly ConcurrentQueue<string> stdout = new(), stderr = new();
        readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Sidecar(TestHost host, string content, Dictionary<string, string>? env = null)
        {
            var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var psi = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "Ofnr.Server.dll"), "--sidecar" }) psi.ArgumentList.Add(a);
            psi.Environment["OFNR_DATA"] = host.DataRoot;
            psi.Environment["OFNR_DB"] = host.ConnectionString;
            psi.Environment["OFNR_TOKEN"] = "sidecar-test-token";
            psi.Environment["OFNR_CONTENT"] = content;
            foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
            Process = new Process { StartInfo = psi };
            Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdout.Enqueue(e.Data);
                if (e.Data.StartsWith("OFNR_READY ")) ready.TrySetResult(e.Data);
            };
            Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Enqueue(e.Data); };
            Process.Start();
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
        }

        public Process Process { get; }
        public IReadOnlyCollection<string> Stdout => stdout;

        /// <summary>The address from the ready line, with a client that carries the token.</summary>
        public async Task<(HttpClient Client, int Port)> ReadyAsync()
        {
            var line = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var m = Regex.Match(line, @"^OFNR_READY (http://127\.0\.0\.1:(\d+))$");
            Assert.True(m.Success, line);
            var client = new HttpClient { BaseAddress = new Uri(m.Groups[1].Value) };
            client.DefaultRequestHeaders.Add("X-Ofnr-Token", "sidecar-test-token");
            return (client, int.Parse(m.Groups[2].Value));
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            TestContext.Current.TestOutputHelper?.WriteLine($"stdout:\n{string.Join('\n', stdout)}\nstderr:\n{string.Join('\n', stderr)}");
            Process.Dispose();
        }
    }

    [Fact]
    public async Task Sidecar_announces_its_bound_port_and_stops_when_stdin_closes()
    {
        await using var host = new TestHost();
        var content = Path.Combine(host.DataRoot, "content");
        Directory.CreateDirectory(Path.Combine(content, "config"));
        File.WriteAllText(Path.Combine(content, "config", "recipes.json"), "[]");
        using var sidecar = new Sidecar(host, content);

        var (client, port) = await sidecar.ReadyAsync();

        Assert.NotEqual(47821, port); // a free port, not the settings port another copy may hold
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health", Ct)).StatusCode); // Host 127.0.0.1:<port> passes
        Assert.Equal("[]", File.ReadAllText(Path.Combine(host.DataRoot, "recipes.json"))); // shipped content came from OFNR_CONTENT

        sidecar.Process.StandardInput.Close();
        await sidecar.Process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, sidecar.Process.ExitCode);
        Assert.Single(sidecar.Stdout, l => l.StartsWith("OFNR_READY"));
    }

    [Fact]
    public async Task Job_left_queued_by_the_last_session_gets_the_bound_port_as_its_mcp_address()
    {
        await using var host = new TestHost();
        // The app quit while one job ran and another waited. Stopping the stale run warms up the start path,
        // which lets the waiting job start before the server has bound its port.
        var job = new Job { RecipeId = "free", Title = "남은 작업", Instruction = "이어서 진행" };
        await using (var db = Db(host))
        {
            await db.Database.MigrateAsync(Ct);
            var stale = new Job { RecipeId = "free", Title = "멈춘 작업", Instruction = "진행 중", Status = JobStatus.Running };
            db.JobRuns.Add(new JobRun { Job = stale, Seq = 1, Status = RunStatus.Working, StartedAt = DateTimeOffset.UtcNow });
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
        }
        using var sidecar = new Sidecar(host, AppContext.BaseDirectory);

        var (_, port) = await sidecar.ReadyAsync();

        // The engine path does not exist, so the run fails right after it wrote its MCP config.
        await JobHost.WaitFor(() =>
        {
            using var db = Db(host);
            return db.Jobs.Single(j => j.Id == job.Id).Status is not (JobStatus.Queued or JobStatus.Running);
        });
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(host.DataRoot, "jobs", $"j-{job.Id:N}", "mcp.json")))!;
        Assert.Equal($"http://127.0.0.1:{port}/mcp", mcp["mcpServers"]!["ofnr"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Programs_the_server_starts_do_not_inherit_the_token_or_the_mail_password()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake pg_dump is a shell script, which Windows cannot start.");
        await using var host = new TestHost();
        // The backup at start runs pg_dump, which the server starts without stripping anything, like an app /api/open opens.
        var envFile = Path.Combine(host.DataRoot, "pg_dump.env");
        var pgDump = Path.Combine(host.DataRoot, "no-pg_dump");
        File.WriteAllText(pgDump, $"#!/bin/sh\nenv > '{envFile}.tmp' && mv '{envFile}.tmp' '{envFile}'\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pgDump, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settingsPath = Path.Combine(host.DataRoot, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        settings["mail"] = new JsonObject { ["address"] = "ceo@ofnr.kr", ["imapHost"] = "127.0.0.1", ["imapPort"] = 1 };
        File.WriteAllText(settingsPath, settings.ToJsonString());
        using var sidecar = new Sidecar(host, AppContext.BaseDirectory, new() { ["OFNR_MAIL_PASSWORD"] = "mail-secret" });

        var (client, _) = await sidecar.ReadyAsync();

        await JobHost.WaitFor(() => File.Exists(envFile));
        var env = File.ReadAllText(envFile);
        Assert.DoesNotContain("OFNR_TOKEN=", env);
        Assert.DoesNotContain("mail-secret", env);
        // The server itself still has both: the token opens /api, and the mail password gets past the password check to the connection.
        var test = await (await client.PostAsync("/api/settings/mail-test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("메일 서버에 연결하지 못했습니다: ", test.GetProperty("detail").GetString());
    }

    static OfnrDb Db(TestHost host) => new(new DbContextOptionsBuilder<OfnrDb>().UseNpgsql(host.ConnectionString).Options);
}
