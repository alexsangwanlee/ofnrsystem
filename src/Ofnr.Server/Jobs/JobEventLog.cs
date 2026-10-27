using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Jobs;

public static class JobEventLog
{
    /// <summary>
    /// Saves the run's next event (seq = max + 1) and pushes it to the job's SignalR group. The job runner and the
    /// report_progress tool write events for the same run at the same time, so every JobEvent must be written here.
    /// </summary>
    /// <param name="raw">Set only by the server's own MCP tools, so their events can be told from what the engine printed.</param>
    public static async Task<JobEventDto> AppendAsync(OfnrDb db, IOpsNotifier notifier, Guid jobId, Guid runId, string kind, string summary, CancellationToken ct = default,
        string? raw = null)
    {
        var e = new JobEvent { JobId = jobId, RunId = runId, Kind = kind, Summary = summary, Raw = raw };
        db.JobEvents.Add(e);
        for (var attempt = 1; ; attempt++)
        {
            e.Seq = (await db.JobEvents.Where(x => x.RunId == runId).MaxAsync(x => (int?)x.Seq, ct) ?? 0) + 1;
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt < 5 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { }
        }
        var dto = new JobEventDto(e.Id, e.JobId, e.RunId, e.Seq, e.Kind, e.Summary, e.At);
        await notifier.JobEvent(dto);
        return dto;
    }
}
