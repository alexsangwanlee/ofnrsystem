using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Ofnr.Server.Agents;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Mail;

public sealed record TriageBatch(TriageItem[] Items);

public sealed record TriageItem(
    string MailId, string Category, string Summary, string? Brand, string? ProjectType, string? Period, string? Venue, string? Area,
    string? Budget, string? Deadline, string? Contact, bool NeedsReply, string[] MissingFields, string? SuggestedProjectId, double Confidence);

/// <summary>Classifies, summarizes and extracts fields from mail with the default engine and no tools. Queued mail is triaged one run at a time.</summary>
public sealed partial class MailTriage(IServiceScopeFactory scopes, SettingsStore settings, AgentFactory agents, IOpsNotifier notifier, ILogger<MailTriage> logger)
    : BackgroundService
{
    internal const string FailedMessage = "메일 분류에 실패했습니다";
    const int BatchSize = 10;
    const int BodyLimit = 4000;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // RunAsync<T> sends a schema without additionalProperties:false, which Codex's strict --output-schema rejects.
    static readonly ChatResponseFormat Format = ChatResponseFormat.ForJsonSchema(
        AIJsonUtilities.CreateJsonSchema(typeof(TriageBatch), serializerOptions: Json,
            inferenceOptions: new() { TransformOptions = new() { DisallowAdditionalProperties = true, RequireAllProperties = true } }),
        "triage_batch");

    static readonly string[] ClaudeArgs = ["--tools", "", "--strict-mcp-config", "--setting-sources", "user"];
    // The read-only sandbox only stops writes; these are the tools codex 0.164.0 exec otherwise offers the model (shell, images, apps, browser, web).
    static readonly string[] CodexArgs = ["--ignore-user-config", "-c", "sandbox_mode=\"read-only\"", "-c", "features.shell_tool=false", "-c", "features.view_image=false",
        "-c", "features.apps=false", "-c", "features.browser_use=false", "-c", "features.computer_use=false", "-c", "web_search=\"disabled\""];

    readonly Channel<IReadOnlyList<Guid>> queue = Channel.CreateUnbounded<IReadOnlyList<Guid>>();

    /// <summary><see cref="FailedMessage"/> and why, after a run that failed; null after one that worked.</summary>
    public string? Failure { get; private set; }

    /// <summary>How long one engine run may take. The queue has one consumer, so a stuck CLI would stop all later triage.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public void Queue(IReadOnlyList<Guid> mailIds) => queue.Writer.TryWrite(mailIds);

    /// <summary>
    /// The queue lives in memory, so incoming mail of the last 30 days still untriaged (queued when the server stopped, or failed) goes back in.
    /// Runs before MailSync starts (registered after this), so new mail is not queued twice.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Current.AutoTriage)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var since = DateTimeOffset.UtcNow.AddDays(-30);
                var ids = await scope.ServiceProvider.GetRequiredService<OfnrDb>().MailMessages
                    .Where(m => m.Category == null && !m.Outgoing && m.ReceivedAt >= since).OrderBy(m => m.ReceivedAt).Select(m => m.Id).ToListAsync(cancellationToken);
                if (ids.Count > 0) Queue(ids);
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Untriaged mail could not be queued"); }
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var ids in queue.Reader.ReadAllAsync(stop))
        {
            try { await TriageAsync(ids, stop); }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                // A database error must not end the loop; later mail still gets triaged.
                logger.LogWarning(e, "Mail triage failed");
                Failure = $"{FailedMessage}: {Reason(e)}";
            }
        }
    }

    /// <summary>Triages in batches of 10. A failed batch stays untriaged and sets <see cref="Failure"/>.</summary>
    public async Task TriageAsync(IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        var o = settings.Current;
        var codex = o.DefaultEngine == "codex";
        var args = new List<string>(codex ? CodexArgs : ClaudeArgs);
        // As for jobs (EngineInvocation); Codex's --ignore-user-config also drops the model in config.toml.
        if ((codex ? o.CodexModel : o.ClaudeModel) is { Length: > 0 } model) args.AddRange([codex ? "-m" : "--model", model]);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var active = await db.Projects.AsNoTracking().Where(p => p.Status == ProjectStatus.Active)
            .Select(p => new { p.Id, p.JobNo, p.Name, Client = p.Client != null ? p.Client.Name : null, Domains = p.Client != null ? p.Client.MailDomains : null })
            .ToListAsync(ct);
        var matchable = active.Select(p => (p.Id, p.JobNo, p.Domains ?? [])).ToList();
        string? failed = null;
        foreach (var batch in mailIds.Chunk(BatchSize))
        {
            var mails = await db.MailMessages.AsNoTracking().Where(m => batch.Contains(m.Id)).ToListAsync(ct);
            if (mails.Count == 0) continue;
            TriageBatch result;
            try
            {
                var prompt = new StringBuilder(SkillBody(File.ReadAllText(Path.Combine(o.PluginDir, "skills", "mail-triage", "SKILL.md"))))
                    .AppendLine().AppendLine().AppendLine("진행 중 프로젝트:");
                foreach (var p in active)
                    prompt.AppendLine($"- id: {p.Id} | 작업번호: {p.JobNo} | 이름: {p.Name} | 거래처: {p.Client ?? "없음"} | 메일 도메인: {string.Join(", ", p.Domains ?? [])}");
                if (active.Count == 0) prompt.AppendLine("- 없음");
                prompt.AppendLine().AppendLine("아래 메일마다 items에 한 항목을 넣으세요. mail_id에는 untrusted_mail 태그의 id를, suggested_project_id에는 위 목록의 id만 쓰세요.");
                foreach (var m in mails) prompt.AppendLine().Append(PromptBuilder.UntrustedMail(m, withId: true, bodyLimit: BodyLimit));

                var agent = agents.Create(o.DefaultEngine, "mail-triage", Path.Combine(o.JobsRoot, "triage"), args);
                // A Codex job may hold the slot; waiting for it is not part of the time limit.
                if (codex) await agents.CodexSlot.WaitAsync(ct);
                AgentResponse response;
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(RunTimeout);
                    response = await agent.RunAsync(prompt.ToString(), options: new() { ResponseFormat = Format }, cancellationToken: limit.Token);
                }
                finally { if (codex) agents.CodexSlot.Release(); }
                result = JsonSerializer.Deserialize<TriageBatch>(response.Text, Json) ?? throw new JsonException("빈 응답");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "Mail triage failed");
                failed = Reason(e);
                continue;
            }

            // Read again: the CEO may have linked or closed a mail while the engine ran.
            var rows = await db.MailMessages.Where(m => batch.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
            var changed = new List<Guid>();
            foreach (var item in result.Items ?? [])
            {
                if (!Guid.TryParse(item.MailId, out var id) || !rows.TryGetValue(id, out var m)) continue;
                m.Category = item.Category;
                m.Summary = item.Summary;
                m.Extracted = JsonSerializer.SerializeToDocument(
                    new { item.Brand, item.ProjectType, item.Period, item.Venue, item.Area, item.Budget, item.Deadline, item.Contact, item.MissingFields }, Json);
                // A job number or client domain is certain; the engine's pick only counts when it names an active project.
                (m.SuggestedProjectId, m.SuggestedConfidence) = MailMapper.MatchProject(m, matchable) is { } sure ? (sure, 1.0)
                    : Guid.TryParse(item.SuggestedProjectId, out var pick) && active.Any(p => p.Id == pick) ? (pick, Math.Clamp(item.Confidence, 0, 1))
                    : ((Guid?)null, (double?)null);
                // Client mail on a project is linked at sync and still needs an answer; a replied or closed mail stays done.
                if (item.NeedsReply && m.Status is MailStatus.New or MailStatus.Linked) m.Status = MailStatus.NeedsReply;
                changed.Add(id);
            }
            await db.SaveChangesAsync(ct);
            foreach (var dto in await db.MailMessages.Where(m => changed.Contains(m.Id)).ToSummaries().ToListAsync(ct)) await notifier.MailUpdated(dto);
        }
        Failure = failed is null ? null : $"{FailedMessage}: {failed}";
    }

    // The installed app keeps no log file, so Health is the only place the CEO or a developer sees why.
    static string Reason(Exception e) => e switch
    {
        OperationCanceledException => "제한 시간을 넘겼습니다.",
        JsonException => "엔진의 답이 정해진 형식이 아닙니다.",
        _ => e.Message.Split('\n')[0].Trim(),
    };

    static string SkillBody(string text) => text[Frontmatter().Match(text).Length..].Trim();

    [GeneratedRegex(@"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();
}
