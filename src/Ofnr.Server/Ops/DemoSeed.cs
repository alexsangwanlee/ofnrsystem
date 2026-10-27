using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Projects;
using Activity = Ofnr.Server.Data.Activity;

namespace Ofnr.Server.Ops;

/// <summary>
/// Sample clients, projects, mail and finished jobs for screenshots and scripts/e2e.sh; runs only with --seed-demo. Every name says 샘플.
/// Nothing is queued or running and every incoming mail is already triaged, so seeding never starts an engine.
/// </summary>
public static class DemoSeed
{
    const string Plan = "01_PLAN";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        if (await db.Clients.AnyAsync(c => c.Name.StartsWith("샘플"))) return;
        var o = scope.ServiceProvider.GetRequiredService<SettingsStore>().Current;
        var d = DateOnly.FromDateTime(DateTime.Now);
        var now = DateTimeOffset.UtcNow;

        Client Client(string name, string domain, string contact) => db.Clients.Add(new Client
        {
            Name = name, MailDomains = [domain], ContactName = contact, ContactEmail = $"contact@{domain}", ContactPhone = "010-0000-0000", Notes = "샘플 거래처입니다.",
        }).Entity;
        var outdoor = Client("샘플 아웃도어", "outdoor.sample.example", "김샘플 과장");
        var coffee = Client("샘플 커피", "coffee.sample.example", "박샘플 팀장");
        var beauty = Client("샘플 뷰티", "beauty.sample.example", "최샘플 대리");
        await db.SaveChangesAsync();

        Task<Project> Project(string name, string slug, Client c, ProjectStage stage, string venue, DateOnly? install, DateOnly? open, DateOnly? end,
            long? quote, long? contract, string? instructions = null) =>
            ProjectEndpoints.CreateAsync(db, o, new ProjectInput(name, slug, c.Id, stage, venue, install, open, end, end?.AddDays(1), quote, contract, null, instructions));
        var popup = await Project("샘플 성수 팝업스토어", "Sample-Seongsu-Popup", outdoor, ProjectStage.Inquiry, "서울 성동구 성수동", d.AddDays(3), d.AddDays(5), d.AddDays(19),
            48_000_000, null, "샘플 지침: 거래처 담당자는 오전 회신을 선호합니다. 견적은 VAT 별도로 적습니다.");
        var showroom = await Project("샘플 브랜드 쇼룸 리뉴얼", "Sample-Showroom", coffee, ProjectStage.Planning, "서울 마포구 연남동", null, d.AddDays(12), null, 32_500_000, null);
        var booth = await Project("샘플 신제품 런칭 부스", "Sample-Launch-Booth", beauty, ProjectStage.Design, "코엑스 B홀", d.AddDays(9), d.AddDays(10), d.AddDays(13), 78_000_000, 75_000_000);
        await Project("샘플 백화점 시즌 행사", "Sample-Dept-Season", outdoor, ProjectStage.Production, "판교 백화점 1층 이벤트홀", d.AddDays(1), d.AddDays(2), d.AddDays(16), 27_000_000, 26_000_000);
        await Project("샘플 전시 운영", "Sample-Exhibition-Ops", coffee, ProjectStage.Operation, "부산 벡스코 2홀", d.AddDays(-4), d.AddDays(-3), d.AddDays(6), 56_000_000, 54_000_000);
        var report = await Project("샘플 결과보고", "Sample-Report", beauty, ProjectStage.Report, "서울 강남구 신사동", d.AddDays(-30), d.AddDays(-28), d.AddDays(-14), 12_500_000, 12_000_000);
        var past = await Project("샘플 지난 팝업", "Sample-Past-Popup", outdoor, ProjectStage.Done, "서울 용산구 한남동", d.AddDays(-80), d.AddDays(-78), d.AddDays(-60), 31_000_000, 30_000_000);
        past.Status = ProjectStatus.Completed;

        // Incoming mail as MailSync and MailTriage leave it; uid order is arrival order.
        var uid = 0;
        MailMessage Mail(string fromName, string from, string subject, string body, Project? project, MailStatus status, string category, string summary, object extracted,
            Project? suggested = null, double? confidence = null, string? html = null, MailAttachment[]? attachments = null, string? threadOf = null) =>
            db.MailMessages.Add(new MailMessage
            {
                Folder = "INBOX", UidValidity = 1, Uid = ++uid, MessageId = $"<sample-{uid}@{from.Split('@')[1]}>", ThreadKey = threadOf ?? $"<sample-{uid}@{from.Split('@')[1]}>",
                FromName = fromName, FromAddress = from, To = "ceo@ofnr.sample.example", Subject = subject, ReceivedAt = now.AddHours(-uid * 5),
                TextBody = body, HtmlBody = html, Attachments = [.. attachments ?? []], ProjectId = project?.Id, Status = status,
                Category = category, Summary = summary, Extracted = JsonSerializer.SerializeToDocument(extracted),
                SuggestedProjectId = suggested?.Id, SuggestedConfidence = confidence,
            }).Entity;
        var inquiry = Mail("김샘플", "kim@outdoor.sample.example", "[샘플] 성수 팝업스토어 견적 요청드립니다",
            "안녕하세요, 샘플 아웃도어 김샘플입니다.\n\n다음 달 성수동에서 2주간 팝업스토어를 열려고 합니다. 약 30평 규모이고 예산은 5천만원 안쪽입니다.\n이번 주 금요일까지 대략적인 견적과 일정을 받아 볼 수 있을까요?\n\n감사합니다.",
            popup, MailStatus.Linked, "quote_request", "샘플 아웃도어가 성수동 2주 팝업스토어(약 30평, 예산 5천만원 이내)의 견적과 일정을 금요일까지 요청했습니다.",
            new { brand = "샘플 아웃도어", project_type = "팝업스토어", period = "다음 달 2주", venue = "성수동", area = "약 30평", budget = "5천만원 이내", deadline = "이번 주 금요일", contact = "김샘플 과장", missing_fields = new[] { "정확한 오픈 날짜", "집기 수량" } });
        Mail("박샘플", "park@coffee.sample.example", "[샘플] 연남동 쇼룸 리뉴얼 일정 문의",
            "안녕하세요. 지난번 말씀드린 연남동 쇼룸 리뉴얼 건으로 연락드립니다.\n공사 가능한 날짜가 바뀌어 일정을 다시 맞춰야 할 것 같습니다. 통화 가능한 시간 알려 주세요.",
            null, MailStatus.New, "feedback", "샘플 커피가 연남동 쇼룸 리뉴얼의 공사 일정 변경을 알리고 통화를 요청했습니다.",
            new { brand = "샘플 커피", project_type = "공간 브랜딩", period = (string?)null, venue = "연남동", area = (string?)null, budget = (string?)null, deadline = (string?)null, contact = "박샘플 팀장", missing_fields = new[] { "변경된 공사 가능 날짜" } },
            showroom, 0.72);
        var newBrand = Mail("이샘플", "lee@newbrand.sample.example", "[샘플] 신규 브랜드 팝업 제안 요청",
            "처음 연락드립니다. 샘플 뉴브랜드 마케팅팀 이샘플입니다.\n내년 봄 시즌 팝업스토어 제안을 받고 싶어 연락드렸습니다. 첨부한 브리프를 참고해 주세요.",
            null, MailStatus.NeedsReply, "inquiry", "샘플 뉴브랜드가 내년 봄 시즌 팝업스토어 제안을 요청했습니다. 브리프가 첨부되어 있습니다.",
            new { brand = "샘플 뉴브랜드", project_type = "팝업스토어", period = "내년 봄 시즌", venue = (string?)null, area = (string?)null, budget = (string?)null, deadline = (string?)null, contact = "이샘플 (마케팅팀)", missing_fields = new[] { "예산", "장소", "면적" } },
            attachments: [new MailAttachment { Index = 0, FileName = "샘플_브리프.pdf", Size = 248_331 }]);
        Mail("최샘플", "choi@beauty.sample.example", "[샘플] 런칭 부스 반입 일정 공유",
            "반입 일정 공유드립니다.", booth, MailStatus.Done, "ops_report", "샘플 뷰티가 코엑스 런칭 부스의 반입 일정을 공유했습니다.",
            new { brand = "샘플 뷰티", project_type = "전시 부스", period = (string?)null, venue = "코엑스 B홀", area = "6m x 3m", budget = (string?)null, deadline = (string?)null, contact = "최샘플 대리", missing_fields = Array.Empty<string>() },
            html: "<p>반입 일정 공유드립니다.</p><ul><li>반입: 행사 전날 18시 이후</li><li>철수: 마지막 날 20시</li></ul><p><a href=\"https://sample.example/plan\" target=\"_blank\" rel=\"noopener\">도면 보기</a></p>");
        db.Activities.AddRange(
            new Activity { ProjectId = popup.Id, Kind = "mail", Text = $"메일을 연결했습니다: {inquiry.Subject}" },
            new Activity { ProjectId = booth.Id, Kind = "mail", Text = "메일을 연결했습니다: [샘플] 런칭 부스 반입 일정 공유" });
        db.MailDrafts.Add(new MailDraft
        {
            ReplyToMailId = newBrand.Id, To = newBrand.FromAddress, Subject = $"RE: {newBrand.Subject}", Author = "ai",
            Body = "이샘플 님, 안녕하세요. 오프너입니다.\n\n제안 요청 감사합니다. 정확한 제안을 위해 예산 범위, 희망 장소, 면적을 알려 주시면 일주일 안에 제안서를 보내 드리겠습니다.\n\n감사합니다.",
        });
        await db.SaveChangesAsync();

        // Finished jobs with the events JobRunner writes: one to review, one waiting on a question, one failed, one blocked, one done.
        async Task<(Job Job, JobRun Run)> Job(string recipeId, Project p, JobStatus status, string instruction, (string Kind, string Summary)[] events,
            RunStatus runStatus = RunStatus.Done, string? error = null, decimal cost = 0.18m, JobResult? result = null, Guid? mailId = null)
        {
            var name = scope.ServiceProvider.GetRequiredService<RecipeCatalog>().Get(recipeId)!.Name;
            var job = db.Jobs.Add(new Job
            {
                ProjectId = p.Id, RecipeId = recipeId, Title = $"{name} · {p.Name}", Instruction = instruction, Status = status, MailId = mailId, Result = result,
                Source = mailId is null ? "manual" : "mail", CreatedAt = now.AddHours(-3), UpdatedAt = now.AddMinutes(-events.Length),
            }).Entity;
            var run = db.JobRuns.Add(new JobRun
            {
                JobId = job.Id, Seq = 1, Status = runStatus, Error = error, EngineVersion = "2.1.311", StartedAt = now.AddHours(-3), EndedAt = now.AddHours(-3).AddMinutes(6),
                CostUsd = cost, InputTokens = 18_240, OutputTokens = 2_115,
            }).Entity;
            db.JobEvents.AddRange(events.Select((e, i) => new JobEvent { JobId = job.Id, RunId = run.Id, Seq = i + 1, Kind = e.Kind, Summary = e.Summary, At = now.AddHours(-3).AddSeconds(i * 20) }));
            await db.SaveChangesAsync();
            return (job, run);
        }

        var briefName = ProjectFolders.NextVersionedName(Path.Combine(popup.FolderPath, Plan), popup.JobNo, "BRIEF", "md");
        var (brief, _) = await Job("brief", popup, JobStatus.Review, "메일 내용으로 요구사항을 정리해 주세요.",
        [
            ("system", "메일 내용으로 요구사항을 정리해 주세요."),
            ("tool", "mcp__ofnr__get_project"),
            ("tool", "mcp__ofnr__get_mail_thread"),
            ("text", "메일과 프로젝트 정보를 읽고 요구사항을 정리하고 있습니다."),
            ("tool", $"Write {Path.Combine(popup.FolderPath, Plan, briefName)}"),
            ("usage", "입력 토큰 18,240, 출력 토큰 2,115, 추정 비용 $0.42"),
            ("result", "성수 팝업스토어 요구사항 정리 초안을 만들었습니다."),
        ], cost: 0.42m, mailId: inquiry.Id, result: new JobResult
        {
            Summary = "성수 팝업스토어 요구사항 정리 초안을 만들었습니다.", Files = [$"{Plan}/{briefName}"],
            NeedsConfirmation = ["정확한 오픈 날짜", "집기 수량"], NextSteps = ["거래처에 빠진 정보를 묻는 회신 보내기", "견적서 작업 맡기기"],
        });
        await Artifact(db, brief, popup, briefName, "# 샘플 성수 팝업스토어 요구사항\n\n- 규모: 약 30평\n- 기간: 다음 달 2주\n- 예산: 5천만원 이내\n\n## 확인 필요\n\n- 정확한 오픈 날짜\n- 집기 수량\n");

        var (quote, quoteRun) = await Job("quote", showroom, JobStatus.NeedsAttention, "쇼룸 리뉴얼 견적서 초안을 만들어 주세요.",
        [
            ("system", "쇼룸 리뉴얼 견적서 초안을 만들어 주세요."),
            ("tool", "mcp__ofnr__get_project"),
            ("tool", "mcp__ofnr__ask_ceo"),
            ("result", "바닥재가 정해지면 견적서를 마저 만들겠습니다."),
        ], result: new JobResult { Summary = "바닥재가 정해지면 견적서를 마저 만들겠습니다.", NeedsConfirmation = ["바닥재 종류"] });
        await scope.ServiceProvider.GetRequiredService<Approvals>()
            .CreateQuestionAsync(quote.Id, quoteRun.Id, "바닥재를 데코타일과 카펫 중 무엇으로 할까요? 단가 차이는 약 120만원입니다.", ["데코타일", "카펫"]);

        await Job("graphic", booth, JobStatus.Failed, "런칭 부스 키 비주얼 시안 두 가지를 만들어 주세요.",
        [
            ("system", "런칭 부스 키 비주얼 시안 두 가지를 만들어 주세요."),
            ("tool", "mcp__ofnr__get_project"),
            ("warning", "Illustrator 응답이 늦습니다."),
            ("error", "작업이 실패했습니다: 제한 시간 60분을 넘겨 중지했습니다."),
        ], RunStatus.Failed, "제한 시간 60분을 넘겨 중지했습니다.");
        await Job("model3d", booth, JobStatus.NeedsAttention, "부스 3D 모델을 만들어 주세요.",
            [("error", "SketchUp을 열고 MCP Server를 Start 해 주세요.")], RunStatus.Stopped, "SketchUp을 열고 MCP Server를 Start 해 주세요.", cost: 0);

        var reportName = ProjectFolders.NextVersionedName(Path.Combine(report.FolderPath, "05_REPORT"), report.JobNo, "REPORT", "md");
        var (done, _) = await Job("report", report, JobStatus.Done, "결과보고서를 만들어 주세요.",
        [
            ("system", "결과보고서를 만들어 주세요."),
            ("tool", "mcp__ofnr__get_project"),
            ("result", "결과보고서를 만들었습니다."),
        ], cost: 0.65m, result: new JobResult { Summary = "결과보고서를 만들었습니다.", Files = [$"05_REPORT/{reportName}"] });
        await Artifact(db, done, report, reportName, "# 샘플 결과보고\n\n- 방문객: 약 4,200명\n- 설문 응답: 312건\n", "05_REPORT", ArtifactStatus.InternalApproved);
    }

    static async Task Artifact(OfnrDb db, Job job, Project p, string fileName, string content, string dir = Plan, ArtifactStatus status = ArtifactStatus.Draft)
    {
        var path = Path.Combine(p.FolderPath, dir, fileName);
        await File.WriteAllTextAsync(path, content);
        var v = ProjectFolders.ParseVersioned(fileName)!.Value;
        var info = new FileInfo(path);
        db.Artifacts.Add(new Artifact
        {
            ProjectId = p.Id, JobId = job.Id, RelativePath = $"{dir}/{fileName}", DocCode = v.docCode, Version = v.version, Status = status,
            Size = info.Length, ModifiedAt = info.LastWriteTimeUtc,
        });
        await db.SaveChangesAsync();
    }
}
