using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Ofnr.Server;
using Ofnr.Server.Ops;
using Ofnr.Server.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<SettingsStore>((kestrel, settings) => kestrel.Listen(IPAddress.Loopback, settings.Current.Port));
builder.Services.AddLocalAccess();

var app = builder.Build();
var settings = app.Services.GetRequiredService<SettingsStore>();
Bootstrap.EnsureDataRoot(settings.Current, AppContext.BaseDirectory);

app.UseLocalAccess();

app.MapGet("/api/health", () => Health.Build(settings.Current));

if (app.Configuration["OFNR_WEB_ROOT"] is { Length: > 0 } webRoot)
{
    var web = new StaticFileOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath(webRoot)) };
    app.UseStaticFiles(web);
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html", web);
}

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"OFNR listening http://127.0.0.1:{settings.Current.Port}"));
app.Run();
