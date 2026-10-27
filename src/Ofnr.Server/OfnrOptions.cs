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
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    static readonly SemaphoreSlim SaveLock = new(1, 1);

    readonly IConfiguration config;
    readonly string dataRoot;
    readonly string path;
    int filePort;
    string fileConnectionString;

    public SettingsStore(IConfiguration config)
    {
        this.config = config;
        dataRoot = Path.GetFullPath(config["OFNR_DATA"] is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? @"C:\OFNR"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ofnr"));
        path = Path.Combine(dataRoot, "settings.json");
        var fromFile = File.Exists(path) ? Load() : null;
        var o = fromFile ?? new();
        filePort = o.Port;
        fileConnectionString = o.ConnectionString;
        Current = Apply(o);
        if (fromFile is null)
        {
            // Pins the first-run D: choice, so a drive that comes and goes cannot move the projects root.
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Current.ProjectsRoot }, Json));
        }
    }

    public OfnrOptions Current { get; private set; }

    public async Task SaveAsync(OfnrOptions options)
    {
        await SaveLock.WaitAsync();
        try
        {
            // Env overrides last for this run only, so the file keeps its own port and connection string.
            if (config["OFNR_PORT"] is not { Length: > 0 }) filePort = options.Port;
            if (config["OFNR_DB"] is not { Length: > 0 }) fileConnectionString = options.ConnectionString;
            var json = JsonSerializer.SerializeToNode(options, Json)!;
            json["port"] = filePort;
            json["connectionString"] = fileConnectionString;
            Directory.CreateDirectory(dataRoot);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json.ToJsonString(Json));
            File.Move(tmp, path, overwrite: true);
            Current = Apply(options);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    OfnrOptions Load()
    {
        try { return JsonSerializer.Deserialize<OfnrOptions>(File.ReadAllText(path), Json) ?? new(); }
        catch (JsonException e) { throw new InvalidDataException($"settings.json 형식이 잘못되었습니다: {path}", e); }
    }

    OfnrOptions Apply(OfnrOptions o)
    {
        o.DataRoot = dataRoot;
        // Only a fixed disk: a DVD, card reader or USB stick at D: comes and goes.
        if (string.IsNullOrWhiteSpace(o.ProjectsRoot))
            o.ProjectsRoot = OperatingSystem.IsWindows() && new DriveInfo("D") is { IsReady: true, DriveType: DriveType.Fixed }
                ? @"D:\OFNR\Projects"
                : Path.Combine(dataRoot, "Projects");
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
