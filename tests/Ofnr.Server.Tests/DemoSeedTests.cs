using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ofnr.Server.Data;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Tests;

public sealed class DemoSeedTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seed_is_labelled_runs_once_and_leaves_nothing_for_an_engine()
    {
        await using var host = new TestHost();
        await DemoSeed.RunAsync(host.Services);
        await DemoSeed.RunAsync(host.Services);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        Assert.All(await db.Clients.Select(c => c.Name).ToListAsync(Ct), n => Assert.StartsWith("샘플 ", n));
        var projects = await db.Projects.ToListAsync(Ct);
        Assert.Equal(7, projects.Count);
        Assert.All(projects, p => Assert.True(p.Name.StartsWith("샘플 ") && Directory.Exists(Path.Combine(p.FolderPath, "00_IN")), p.Name));
        // Untriaged incoming mail or a queued job would start a real engine at the next start.
        Assert.All(await db.MailMessages.ToListAsync(Ct), m => Assert.True(m.Subject.StartsWith("[샘플] ") && m.Category is not null, m.Subject));
        Assert.DoesNotContain(await db.Jobs.Select(j => j.Status).ToListAsync(Ct), s => s is JobStatus.Queued or JobStatus.Running);
        Assert.All(await db.Artifacts.Include(a => a.Project).ToListAsync(Ct), a => Assert.True(File.Exists(Path.Combine(a.Project.FolderPath, a.RelativePath)), a.RelativePath));

        var today = await host.Authed().GetFromJsonAsync<JsonElement>("/api/today", Ct);
        var question = Assert.Single(today.GetProperty("attention").EnumerateArray(), a => a.GetProperty("approvalKind").GetString() == "question");
        Assert.Equal(["데코타일", "카펫"], JsonElement.Parse(question.GetProperty("detail").GetString()!).GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["blocked", "failed", "review"], today.GetProperty("attention").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).Where(k => k != "approval").Order());
    }
}
