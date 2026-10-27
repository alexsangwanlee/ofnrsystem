using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Jobs;

/// <summary>Questions the AI leaves for the CEO (ask_ceo) and his answers. Task 6 turns an answer into a follow-up run.</summary>
public sealed class Approvals(OfnrDb db, IOpsNotifier notifier)
{
    static readonly string[] Decisions = ["accept", "edit", "respond", "ignore"];

    public async Task<ApprovalDto> CreateQuestionAsync(Guid jobId, Guid? runId, string question, string[]? options)
    {
        var a = new Approval
        {
            JobId = jobId,
            RunId = runId,
            Kind = "question",
            Title = question.Trim(),
            Detail = string.Join("\n", (options ?? []).Select((o, i) => $"{i + 1}. {o.Trim()}")),
        };
        db.Approvals.Add(a);
        await db.SaveChangesAsync();
        var dto = ApprovalDto.From(a);
        await notifier.ApprovalRequested(dto);
        await notifier.Notify("확인이 필요합니다", a.Title, $"/jobs/{jobId}");
        return dto;
    }

    /// <summary>Records the CEO's answer once.</summary>
    /// <exception cref="ArgumentException">Unknown decision, or edit/respond without text (400).</exception>
    /// <exception cref="KeyNotFoundException">No such approval (404).</exception>
    /// <exception cref="InvalidOperationException">Already decided or expired (409).</exception>
    public async Task<ApprovalDto> DecideAsync(Guid approvalId, string decision, string? text)
    {
        if (!Decisions.Contains(decision)) throw new ArgumentException("알 수 없는 응답입니다.");
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (decision is "edit" or "respond" && text is null) throw new ArgumentException("답변 내용을 입력해 주세요.");

        var now = DateTimeOffset.UtcNow;
        // Conditional update: a double click must not start two follow-up runs.
        var updated = await db.Approvals.Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Status, ApprovalStatus.Decided)
            .SetProperty(a => a.Decision, decision)
            .SetProperty(a => a.ResponseText, text)
            .SetProperty(a => a.DecidedAt, now));
        if (updated == 0)
            throw await db.Approvals.AnyAsync(a => a.Id == approvalId)
                ? new InvalidOperationException("이미 처리된 확인 요청입니다.")
                : new KeyNotFoundException("확인 요청을 찾을 수 없습니다.");

        var dto = ApprovalDto.From(await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == approvalId));
        await notifier.ApprovalResolved(dto);
        return dto;
    }
}
