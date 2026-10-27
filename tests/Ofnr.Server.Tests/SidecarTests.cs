using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Ofnr.Server.Tests;

/// <summary>The built server as Tauri runs it: <c>--sidecar</c>, pipes for stdin and stdout.</summary>
public sealed class SidecarTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sidecar_announces_its_bound_port_and_stops_when_stdin_closes()
    {
        await using var host = new TestHost(); // only its data root and database; its own in-memory server never starts
        var content = Path.Combine(host.DataRoot, "content");
        Directory.CreateDirectory(Path.Combine(content, "config"));
        File.WriteAllText(Path.Combine(content, "config", "recipes.json"), "[]");

        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "Ofnr.Server.dll"), "--sidecar" }) psi.ArgumentList.Add(a);
        psi.Environment["OFNR_DATA"] = host.DataRoot;
        psi.Environment["OFNR_DB"] = host.ConnectionString;
        psi.Environment["OFNR_TOKEN"] = "sidecar-test-token";
        psi.Environment["OFNR_CONTENT"] = content;

        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.Enqueue(e.Data);
            if (e.Data.StartsWith("OFNR_READY ")) ready.TrySetResult(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Enqueue(e.Data); };
        p.Start();
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var line = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            var m = Regex.Match(line, @"^OFNR_READY (http://127\.0\.0\.1:(\d+))$");
            Assert.True(m.Success, line);
            Assert.NotEqual(47821, int.Parse(m.Groups[2].Value)); // a free port, not the settings port another copy may hold
            using var client = new HttpClient { BaseAddress = new Uri(m.Groups[1].Value) };
            client.DefaultRequestHeaders.Add("X-Ofnr-Token", "sidecar-test-token");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health", Ct)).StatusCode); // Host 127.0.0.1:<port> passes
            Assert.Equal("[]", File.ReadAllText(Path.Combine(host.DataRoot, "recipes.json"))); // shipped content came from OFNR_CONTENT

            p.StandardInput.Close();
            await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(0, p.ExitCode);
            Assert.Single(stdout, l => l.StartsWith("OFNR_READY"));
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            TestContext.Current.TestOutputHelper?.WriteLine($"stdout:\n{string.Join('\n', stdout)}\nstderr:\n{string.Join('\n', stderr)}");
        }
    }
}
