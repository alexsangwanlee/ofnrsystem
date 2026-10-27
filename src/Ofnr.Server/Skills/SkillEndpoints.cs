using static Ofnr.Server.Projects.ProjectEndpoints;

namespace Ofnr.Server.Skills;

public static class SkillEndpoints
{
    public static void MapSkillEndpoints(this WebApplication app)
    {
        app.MapGet("/api/skills", (SkillStore skills) => skills.List());
        app.MapGet("/api/skills/{name}", (SkillStore skills, string name) => Answer(() => Task.FromResult<object>(skills.Read(name))));
        app.MapPut("/api/skills/{name}", (SkillStore skills, string name, SkillInput input) =>
            Answer(async () => await skills.WriteAsync(name, input.Content ?? "")));
        app.MapGet("/api/skills/{name}/history", (SkillStore skills, string name) => Answer(() => Task.FromResult<object>(skills.History(name))));
        app.MapPost("/api/skills/{name}/revert", (SkillStore skills, string name, RevertInput input) =>
            Answer(async () => await skills.RevertAsync(name, input.Commit ?? "")));
    }

    static async Task<IResult> Answer(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (KeyNotFoundException e) { return Error(404, e.Message); }
        catch (ArgumentException e) { return Error(400, e.Message); }
    }
}
