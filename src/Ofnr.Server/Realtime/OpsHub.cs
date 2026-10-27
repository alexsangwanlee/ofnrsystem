using Microsoft.AspNetCore.SignalR;

namespace Ofnr.Server.Realtime;

public sealed class OpsHub : Hub
{
    public Task JoinJob(Guid jobId) => Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task LeaveJob(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");
}

public interface IOpsNotifier
{
    Task JobUpdated(JobDto job);
    Task JobEvent(JobEventDto e);
    Task ApprovalRequested(ApprovalDto approval);
    Task ApprovalResolved(ApprovalDto approval);
    Task MailUpdated(MailSummaryDto mail);
    Task ProjectUpdated(ProjectDto project);
    Task Notify(string title, string body, string? link);
}

public sealed class OpsNotifier(IHubContext<OpsHub> hub) : IOpsNotifier
{
    public Task JobUpdated(JobDto job) => hub.Clients.All.SendAsync("jobUpdated", job);
    public Task JobEvent(JobEventDto e) => hub.Clients.Group($"job:{e.JobId}").SendAsync("jobEvent", e);
    public Task ApprovalRequested(ApprovalDto approval) => hub.Clients.All.SendAsync("approvalRequested", approval);
    public Task ApprovalResolved(ApprovalDto approval) => hub.Clients.All.SendAsync("approvalResolved", approval);
    public Task MailUpdated(MailSummaryDto mail) => hub.Clients.All.SendAsync("mailUpdated", mail);
    public Task ProjectUpdated(ProjectDto project) => hub.Clients.All.SendAsync("projectUpdated", project);
    public Task Notify(string title, string body, string? link) => hub.Clients.All.SendAsync("notify", new { title, body, link });
}
