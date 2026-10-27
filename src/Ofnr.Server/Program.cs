using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using ModelContextProtocol.AspNetCore;
using Npgsql;
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

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton<MailSync>();
builder.Services.AddSingleton<IMailSync>(sp => sp.GetRequiredService<MailSync>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailSync>());
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
Bootstrap.EnsureDataRoot(settings.Current, AppContext.BaseDirectory);
try { app.Services.GetRequiredService<RecipeCatalog>(); }
catch (Exception e) when (e is InvalidDataException or IOException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
string? dbError = null;
try
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OfnrDb>().Database.MigrateAsync();
}
catch (NpgsqlException e)
{
    // Keeps serving so the first-run check screen can say PostgreSQL is missing or stopped.
    dbError = $"데이터베이스에 연결하지 못했습니다: {e.Message}";
    Console.Error.WriteLine(dbError);
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

app.MapGet("/api/health", (IMailSync mail) => Health.Build(settings.Current, dbError, mail.Health));
app.MapProjectEndpoints();
app.MapMailEndpoints();
app.MapJobEndpoints();
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

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"OFNR listening http://127.0.0.1:{settings.Current.Port}"));
app.Run();
return 0;
