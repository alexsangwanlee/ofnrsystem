using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using ModelContextProtocol.AspNetCore;
using Ofnr.Server;
using Ofnr.Server.Agents;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Mail;
using Ofnr.Server.Mcp;
using Ofnr.Server.Ops;
using Ofnr.Server.Projects;
using Ofnr.Server.Realtime;
using Ofnr.Server.Security;
using Ofnr.Server.Skills;

var builder = WebApplication.CreateBuilder(args);
// The desktop app (desktop/src-tauri/src/lib.rs) starts the server with --sidecar: any free port, announced on stdout.
var sidecar = args.Contains("--sidecar");
if (sidecar)
{
    builder.Configuration["OFNR_PORT"] = "0"; // like an env override, so settings.json keeps its own port
    // Tauri reads the pipes as UTF-8; the Korean console code page 949 would garble Korean errors.
    try { Console.OutputEncoding = Encoding.UTF8; }
    catch (IOException) { } // no console at all, and then .NET writes UTF-8 anyway
}
// Request logs would print the SignalR ?access_token= query.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<SettingsStore>((kestrel, settings) => kestrel.Listen(IPAddress.Loopback, settings.Current.Port));
builder.Services.AddLocalAccess();
// Integers would bind to undefined enum values such as stage 99.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
// Binding failures throw in every environment so the handler below can answer them in the { error } shape.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddSingleton<IOpsNotifier, OpsNotifier>();
MailMapper.RegisterKoreanEncodings();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddDbContext<OfnrDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<SecretStore>().WithDbPassword(sp.GetRequiredService<SettingsStore>().Current.ConnectionString)));
builder.Services.AddSingleton<JobTokens>();
builder.Services.AddScoped<Approvals>();
// Loaded on first use, after Bootstrap has copied the shipped recipes.json into the data root.
builder.Services.AddSingleton(sp => RecipeCatalog.Load(Path.Combine(sp.GetRequiredService<SettingsStore>().Current.DataRoot, "recipes.json")));
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
builder.Services.AddScoped<IImapSource, MailKitImapSource>();
builder.Services.AddScoped<ISmtpTransport, MailKitSmtp>();
builder.Services.AddScoped<MailSender>();
builder.Services.AddSingleton<MailTriage>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailTriage>());
builder.Services.AddSingleton<MailSync>();
builder.Services.AddSingleton<IMailSync>(sp => sp.GetRequiredService<MailSync>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailSync>());
builder.Services.AddSingleton<SkillStore>();
builder.Services.AddSingleton<IDumpRunner, PgDumpRunner>();
builder.Services.AddSingleton<Backup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Backup>());
builder.Services.AddSingleton<Health>();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<OfnrTools>();

WebApplication app;
SettingsStore settings;
try
{
    app = builder.Build(); // resolves SettingsStore for the Kestrel port
    settings = app.Services.GetRequiredService<SettingsStore>();
}
catch (InvalidDataException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
// The installed app's resources folder; a plain build carries config/ and claude-plugin/ next to the server.
Bootstrap.EnsureDataRoot(settings.Current, app.Configuration["OFNR_CONTENT"] is { Length: > 0 } content ? content : AppContext.BaseDirectory);
try { app.Services.GetRequiredService<RecipeCatalog>(); }
catch (Exception e) when (e is InvalidDataException or IOException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
var health = app.Services.GetRequiredService<Health>();
var step = "connect";
try
{
    using var scope = app.Services.CreateScope();
    var database = scope.ServiceProvider.GetRequiredService<OfnrDb>().Database;
    // A rollback point before an update changes the structure of a database in use; a new database has nothing to lose.
    if ((await database.GetPendingMigrationsAsync()).Any() && (await database.GetAppliedMigrationsAsync()).Any())
    {
        step = "backup";
        await app.Services.GetRequiredService<Backup>().RunOnceAsync("pre-migrate");
    }
    step = "migrate";
    await database.MigrateAsync();
}
catch (Exception e)
{
    // Keeps serving so the check screen can say PostgreSQL is missing or stopped, or why the update was not applied.
    // A connection error is not kept: once PostgreSQL runs, the live check says what is left.
    health.MigrateError = step switch
    {
        "backup" => $"업데이트 전에 데이터베이스를 백업하지 못해 업데이트를 적용하지 않았습니다. 설정에서 pg_dump 경로를 확인하고 앱을 다시 시작해 주세요: {e.Message}",
        "migrate" => $"데이터베이스 업데이트를 적용하지 못했습니다: {e.Message}",
        _ => null,
    };
    Console.Error.WriteLine(health.MigrateError ?? $"데이터베이스에 연결하지 못했습니다: {e.Message}");
}

app.UseLocalAccess();
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status400BadRequest && !ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "요청 형식이 올바르지 않습니다." });
    }
});

app.MapOpsEndpoints();
app.MapProjectEndpoints();
app.MapMailEndpoints();
app.MapJobEndpoints();
app.MapSkillEndpoints();
app.MapHub<OpsHub>("/hubs/ops");
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/mcp"), mcp => mcp.Use(JobTokens.Authenticate));
app.MapMcp("/mcp");

if (app.Configuration["OFNR_WEB_ROOT"] is { Length: > 0 } webRoot)
{
    var web = new StaticFileOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath(webRoot)) };
    app.UseStaticFiles(web);
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html", web);
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!sidecar)
    {
        Console.WriteLine($"OFNR listening http://127.0.0.1:{settings.Current.Port}");
        return;
    }
    // Before the ready line: the Host check and the jobs' MCP address both use this port.
    var port = new Uri(app.Urls.Single()).Port;
    app.Configuration["OFNR_PORT"] = port.ToString(CultureInfo.InvariantCulture);
    settings.Current.Port = port;
    Console.WriteLine($"OFNR_READY http://127.0.0.1:{port}");
    // Stdin closes when the app exits or crashes, so the server never outlives it.
    new Thread(() =>
    {
        try { Console.OpenStandardInput().CopyTo(Stream.Null); }
        catch (IOException) { } // a broken pipe is a closed one
        app.Lifetime.StopApplication();
    }) { IsBackground = true }.Start();
});
app.Run();
return 0;
