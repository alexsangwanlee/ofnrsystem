using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Ofnr.Server;
using Ofnr.Server.Data;
using Ofnr.Server.Ops;
using Ofnr.Server.Security;

var builder = WebApplication.CreateBuilder(args);
// Request logs would print the SignalR ?access_token= query.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<SettingsStore>((kestrel, settings) => kestrel.Listen(IPAddress.Loopback, settings.Current.Port));
builder.Services.AddLocalAccess();
builder.Services.AddDbContext<OfnrDb>((sp, o) => o.UseNpgsql(sp.GetRequiredService<SettingsStore>().Current.ConnectionString));

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

app.MapGet("/api/health", () => Health.Build(settings.Current, dbError));

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
