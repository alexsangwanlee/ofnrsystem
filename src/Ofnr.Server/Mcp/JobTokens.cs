using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Ofnr.Server.Mcp;

/// <summary>Per-job bearer tokens for <c>/mcp</c>. In memory only: a server restart ends every token, and Task 6 stops those runs.</summary>
public sealed class JobTokens
{
    public const string JobClaim = "ofnr_job";

    // Keyed by SHA-256 so lookup timing says nothing about the token itself.
    readonly ConcurrentDictionary<string, Guid> jobs = new();

    public string Issue(Guid jobId)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        jobs[Hash(token)] = jobId;
        return token;
    }

    public void Revoke(Guid jobId)
    {
        foreach (var (hash, id) in jobs)
            if (id == jobId) jobs.TryRemove(hash, out _);
    }

    public Guid? Resolve(string bearer) => jobs.TryGetValue(Hash(bearer), out var id) ? id : null;

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Middleware for <c>/mcp</c>: 401 without a live job token, else the request's user carries the job id.</summary>
    public static Task Authenticate(HttpContext ctx, RequestDelegate next)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || ctx.RequestServices.GetRequiredService<JobTokens>().Resolve(auth[7..].Trim()) is not { } jobId)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JobClaim, jobId.ToString())], "JobToken"));
        return next(ctx);
    }
}
