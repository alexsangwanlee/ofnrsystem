using System.Text.Json;

namespace Ofnr.Server;

public sealed class OfnrOptions
{
    public string DataRoot { get; set; } = "";
    public string ProjectsRoot { get; set; } = "";
    public string JobsRoot { get; set; } = "";
    public string PluginDir { get; set; } = "";
    public string TemplatesDir { get; set; } = "";
    public string BackupsDir { get; set; } = "";
    public int Port { get; set; } = 47821;
    public string ConnectionString { get; set; } = "Host=127.0.0.1;Database=ofnr;Username=ofnr;Password=ofnr_dev";
    public string ClaudeExe { get; set; } = "claude";
    public string CodexExe { get; set; } = "codex";
    public string DefaultEngine { get; set; } = "claude";
    public int MaxConcurrentJobs { get; set; } = 2;
    public int MailSyncMinutes { get; set; } = 3;
    public bool AutoTriage { get; set; } = true;
    public bool AppendSentCopy { get; set; }
    public string? ClaudeModel { get; set; }
    public string? CodexModel { get; set; }
    public string PgDumpPath { get; set; } = "pg_dump";
    public MailAccount Mail { get; set; } = new();
}

public sealed class MailAccount
{
    public string Address { get; set; } = "";
    public string ImapHost { get; set; } = "imap.worksmobile.com";
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = "smtp.worksmobile.com";
    public int SmtpPort { get; set; } = 465;
    public string? SentFolder { get; set; }
}

/// <summary>Reads and writes <c>&lt;data&gt;/settings.json</c>; env OFNR_DATA, OFNR_PORT, OFNR_DB win over the file.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly IConfiguration config;
    readonly string dataRoot;
    readonly string path;

    public SettingsStore(IConfiguration config)
    {
        this.config = config;
        dataRoot = Path.GetFullPath(config["OFNR_DATA"] is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? @"C:\OFNR"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ofnr"));
        path = Path.Combine(dataRoot, "settings.json");
        Current = Apply(File.Exists(path) ? JsonSerializer.Deserialize<OfnrOptions>(File.ReadAllText(path), Json) ?? new() : new());
    }

    public OfnrOptions Current { get; private set; }

    public async Task SaveAsync(OfnrOptions options)
    {
        Directory.CreateDirectory(dataRoot);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(options, Json));
        File.Move(tmp, path, overwrite: true);
        Current = Apply(options);
    }

    OfnrOptions Apply(OfnrOptions o)
    {
        o.DataRoot = dataRoot;
        if (string.IsNullOrWhiteSpace(o.ProjectsRoot))
            o.ProjectsRoot = OperatingSystem.IsWindows() && Directory.Exists(@"D:\") ? @"D:\OFNR\Projects" : Path.Combine(dataRoot, "Projects");
        if (string.IsNullOrWhiteSpace(o.JobsRoot)) o.JobsRoot = Path.Combine(dataRoot, "jobs");
        if (string.IsNullOrWhiteSpace(o.PluginDir)) o.PluginDir = Path.Combine(dataRoot, "claude-plugin");
        if (string.IsNullOrWhiteSpace(o.TemplatesDir)) o.TemplatesDir = Path.Combine(dataRoot, "templates");
        if (string.IsNullOrWhiteSpace(o.BackupsDir)) o.BackupsDir = Path.Combine(dataRoot, "backups");
        o.Mail ??= new();
        if (config["OFNR_PORT"] is { Length: > 0 } port) o.Port = int.Parse(port);
        if (config["OFNR_DB"] is { Length: > 0 } db) o.ConnectionString = db;
        return o;
    }
}
