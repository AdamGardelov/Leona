using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Sites the current profile always allows read_page to open. Commands, edits, deletions and memories
// still ask every time; only opening a page can be allowed for good.
public class TrustedSiteService(ChatDb db)
{
    private const int MaxSites = 200;

    public async Task<IReadOnlyList<TrustedSite>> ListAsync(CancellationToken ct) =>
        await db.TrustedSites.AsNoTracking().OrderBy(s => s.Host).ToListAsync(ct);

    // Accepts an address or a bare host such as "liseberg.se". Returns the saved site; adding one twice
    // returns the existing site.
    public async Task<TrustedSite> AddAsync(string address, CancellationToken ct)
    {
        var host = HostOf(address) ?? throw new ArgumentException("Enter a website such as liseberg.se.");
        var existing = await db.TrustedSites.FirstOrDefaultAsync(s => s.Host == host, ct);
        if (existing is not null)
            return existing;
        if (await db.TrustedSites.CountAsync(ct) >= MaxSites)
            throw new ArgumentException($"You can trust at most {MaxSites} sites.");

        var site = new TrustedSite { Host = host };
        db.TrustedSites.Add(site);
        await db.SaveChangesAsync(ct);
        return site;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.TrustedSites.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0;

    // "https://www.Liseberg.se/priser" and "liseberg.se" both give "liseberg.se". Only web addresses
    // with a dotted host count.
    public static string? HostOf(string address)
    {
        var text = address.Trim();
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host.Contains('.') ? host : null;
    }

    public static bool Covers(IEnumerable<string> sites, string url)
    {
        if (HostOf(url) is not { } host || !url.Contains("://", StringComparison.Ordinal))
            return false;

        return sites.Any(site => host == site || host.EndsWith("." + site, StringComparison.Ordinal));
    }
}
