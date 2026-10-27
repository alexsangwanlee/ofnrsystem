using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Tests;

public sealed class ApprovalTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class RecordingNotifier : IOpsNotifier
    {
        public List<object> Calls { get; } = [];
        public Task JobUpdated(JobDto job) => Add(job);
        public Task JobEvent(JobEventDto e) => Add(e);
        public Task ApprovalRequested(ApprovalDto approval) => Add(("requested", approval));
        public Task ApprovalResolved(ApprovalDto approval) => Add(("resolved", approval));
        public Task MailUpdated(MailSummaryDto mail) => Add(mail);
        public Task ProjectUpdated(ProjectDto project) => Add(project);
        public Task Notify(string title, string body, string? link) => Add((title, body, link));
        Task Add(object call) { Calls.Add(call); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Decide_records_decision_and_notifies()
    {
        using var t = TestDb.Create();
        await using var db = t.Context();
        var job = new Job { RecipeId = "free", Title = "자유 지시" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Ct);
        var notifier = new RecordingNotifier();
        var approvals = new Approvals(db, notifier);

        var q = await approvals.CreateQuestionAsync(job.Id, null, "오픈일을 11월 20일로 볼까요?", null);

        Assert.Equal(("question", ApprovalStatus.Pending, ""), (q.Kind, q.Status, q.Detail));
        Assert.Equal([("requested", q), ("확인이 필요합니다", "오픈일을 11월 20일로 볼까요?", $"/jobs/{job.Id}")], notifier.Calls);

        var decided = await approvals.DecideAsync(q.Id, "respond", "네, 11월 20일입니다.");

        Assert.Equal((ApprovalStatus.Decided, "respond"), (decided.Status, decided.Decision));
        Assert.Equal(("resolved", decided), notifier.Calls[^1]);
        var row = await t.Context().Approvals.SingleAsync(a => a.Id == q.Id, Ct);
        Assert.Equal("네, 11월 20일입니다.", row.ResponseText);
        Assert.NotNull(row.DecidedAt);

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => approvals.DecideAsync(q.Id, "accept", null));
        Assert.Equal("이미 처리된 확인 요청입니다.", again.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => approvals.DecideAsync(Guid.NewGuid(), "accept", null));
        await Assert.ThrowsAsync<ArgumentException>(() => approvals.DecideAsync(q.Id, "send", null));
        await Assert.ThrowsAsync<ArgumentException>(() => approvals.DecideAsync(q.Id, "respond", " "));
    }
}
