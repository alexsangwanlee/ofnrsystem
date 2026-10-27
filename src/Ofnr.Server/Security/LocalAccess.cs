using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ofnr.Server.Security;

/// <summary>Per-launch token and Host check so other web pages in the browser cannot drive the local server.</summary>
public static class LocalAccess
{
    public const string CorsPolicy = "ofnr";

    public static readonly string Token = Environment.GetEnvironmentVariable("OFNR_TOKEN") is { Length: > 0 } t
        ? t
        : Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    static readonly byte[] TokenBytes = Encoding.UTF8.GetBytes(Token);

    public static IServiceCollection AddLocalAccess(this IServiceCollection services) =>
        services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .WithOrigins("http://tauri.localhost", "https://tauri.localhost", "tauri://localhost", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

    public static WebApplication UseLocalAccess(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();

        app.UseCors(CorsPolicy);
        app.Use(async (ctx, next) =>
        {
            // Read per request: the desktop sidecar learns its port only once Kestrel has bound it.
            var port = settings.Current.Port;
            if (!new[] { $"127.0.0.1:{port}", $"localhost:{port}" }.Contains(ctx.Request.Host.Value, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "허용되지 않은 접근입니다." });
                return;
            }

            var path = ctx.Request.Path;
            var isHub = path.StartsWithSegments("/hubs");
            if (isHub || path.StartsWithSegments("/api"))
            {
                string? token = ctx.Request.Headers["X-Ofnr-Token"];
                if (string.IsNullOrEmpty(token) && isHub) token = ctx.Request.Query["access_token"];
                if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), TokenBytes))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "인증 토큰이 없거나 맞지 않습니다." });
                    return;
                }
            }

            await next();
        });
        return app;
    }
}
