namespace Ofnr.Server.Ops;

public sealed record CheckDto(string Name, bool Ok, string Detail);

public sealed record HealthDto(string Version, string DataRoot, string ProjectsRoot, CheckDto[] Paths);

public static class Health
{
    public static HealthDto Build(OfnrOptions o) => new(
        typeof(Health).Assembly.GetName().Version!.ToString(3),
        o.DataRoot,
        o.ProjectsRoot,
        [AsciiPath("프로젝트 폴더", o.ProjectsRoot), AsciiPath("작업 폴더", o.JobsRoot)]);

    // The engine CLIs break on non-ASCII paths, so a Korean folder name is an error, not a warning.
    static CheckDto AsciiPath(string name, string path) => path.All(char.IsAscii)
        ? new(name, true, path)
        : new(name, false, $"경로에 영문이 아닌 문자가 있습니다. 영문 경로로 바꿔 주세요: {path}");
}
