using System.Diagnostics;
using Npgsql;
using Ofnr.Server.Mail;

namespace Ofnr.Server.Ops;

/// <summary>Writes one database dump to a file. A seam so tests do not need pg_dump.</summary>
public interface IDumpRunner
{
    Task DumpAsync(string file, CancellationToken ct);
}

/// <summary>pg_dump -Fc of the app database; the password goes through PGPASSWORD, never the command line.</summary>
public sealed class PgDumpRunner(SettingsStore settings, SecretStore secrets) : IDumpRunner
{
    public async Task DumpAsync(string file, CancellationToken ct)
    {
        var o = settings.Current;
        var db = new NpgsqlConnectionStringBuilder(secrets.WithDbPassword(o.ConnectionString));
        var psi = new ProcessStartInfo(o.PgDumpPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // -w: fail instead of waiting for a password prompt nobody sees.
        foreach (var a in new[] { "-Fc", "-w", "-f", file, "-h", db.Host ?? "127.0.0.1", "-p", db.Port.ToString() }) psi.ArgumentList.Add(a);
        if (db.Username is { Length: > 0 } user) { psi.ArgumentList.Add("-U"); psi.ArgumentList.Add(user); }
        psi.ArgumentList.Add(db.Database ?? "ofnr_ops");
        if (db.Password is { Length: > 0 } password) psi.Environment["PGPASSWORD"] = password;
        // pg_dump on Korean Windows prints its errors in CP949, which the UTF-8 read below would garble; C means plain English.
        psi.Environment["LC_MESSAGES"] = "C";

        using var p = Process.Start(psi)!;
        using var kill = ct.Register(() =>
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
        });
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"pg_dump이 종료 코드 {p.ExitCode}로 끝났습니다: {(await err).Trim()}");
    }
}

/// <summary>At start and every 6 hours, dumps the database when the newest dump is a day old, and keeps the 14 newest.</summary>
public sealed class Backup(SettingsStore settings, IDumpRunner dump, ILogger<Backup> logger) : BackgroundService
{
    const int Keep = 14;
    static readonly TimeSpan Every = TimeSpan.FromHours(6), MaxAge = TimeSpan.FromHours(24);

    string? lastError;

    public CheckDto Health => lastError is null ? new("백업", true, settings.Current.BackupsDir) : new("백업", false, lastError);

    /// <summary>Dumps now to <c>ofnr-&lt;yyyyMMdd-HHmm&gt;.dump</c>; a failure leaves no partial file and shows in <see cref="Health"/>.</summary>
    /// <param name="label">Why, for the log: daily or pre-migrate.</param>
    public async Task RunOnceAsync(string label, CancellationToken ct = default)
    {
        var dir = settings.Current.BackupsDir;
        var file = Path.Combine(dir, $"ofnr-{DateTime.Now:yyyyMMdd-HHmm}.dump");
        try
        {
            Directory.CreateDirectory(dir);
            await dump.DumpAsync(file, ct);
            lastError = null;
        }
        catch (Exception e)
        {
            if (File.Exists(file)) File.Delete(file); // a partial dump would count as today's backup
            lastError = $"데이터베이스를 백업하지 못했습니다: {e.Message}";
            throw;
        }
        logger.LogInformation("Backup ({Label}) written to {File}", label, file);
        foreach (var old in Dumps(dir).Skip(Keep)) old.Delete();
    }

    /// <summary>True when it dumped because the newest dump was older than a day.</summary>
    public async Task<bool> RunIfDueAsync(CancellationToken ct = default)
    {
        if (Dumps(settings.Current.BackupsDir).FirstOrDefault() is { } newest && DateTime.UtcNow - newest.LastWriteTimeUtc < MaxAge) return false;
        await RunOnceAsync("daily", ct);
        return true;
    }

    static IEnumerable<FileInfo> Dumps(string dir) => Directory.Exists(dir)
        ? new DirectoryInfo(dir).EnumerateFiles("ofnr-*.dump").OrderByDescending(f => f.LastWriteTimeUtc)
        : [];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield(); // pg_dump must not hold up server start
        while (!stop.IsCancellationRequested)
        {
            try { await RunIfDueAsync(stop); }
            catch (Exception e) when (!stop.IsCancellationRequested) { logger.LogWarning("Backup failed: {Message}", e.Message); }
            try { await Task.Delay(Every, stop); }
            catch (OperationCanceledException) { return; }
        }
    }
}
