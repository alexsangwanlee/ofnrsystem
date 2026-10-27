using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;

namespace Ofnr.Server.Projects;

public static class JobNumbers
{
    /// <summary>Next <c>OFNR-YYNNN</c> for the year of <paramref name="now"/>; callers retry once on a unique violation.</summary>
    public static async Task<string> NextAsync(OfnrDb db, DateTimeOffset now)
    {
        var prefix = $"OFNR-{now.Year % 100:D2}";
        var taken = await db.Projects.Where(p => p.JobNo.StartsWith(prefix)).Select(p => p.JobNo).ToListAsync();
        var max = taken.Select(n => int.TryParse(n.AsSpan(prefix.Length), out var seq) ? seq : 0).DefaultIfEmpty().Max();
        return $"{prefix}{max + 1:D3}";
    }
}
