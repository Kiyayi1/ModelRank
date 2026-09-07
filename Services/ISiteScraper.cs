using Microsoft.Playwright;
using ModelRank.Models;

namespace ModelRank.Services;

public interface ISiteScraper
{
    // API-backed scrapers don't need a Playwright browser at all; MonitoringService
    // skips launching one for them when this is false.
    bool RequiresBrowser => true;

    Task<List<string>> FindModelRankAsync(IPage? page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    // True for scrapers whose site exposes its whole listing in a handful of requests (e.g.
    // Chaturbate's room-list API). MonitoringService uses this to run ONE shared poll per site
    // that updates every tracked model from a single fetch, instead of one independent poll (and
    // one full listing sweep) per model — which is both wasteful and, once several models are
    // tracked at once, enough concurrent load to get the site's API to rate-limit this app.
    bool SupportsBulkFetch => false;

    // Fetches the entire current listing in one shot, keyed by username (case-insensitive).
    // Only called when SupportsBulkFetch is true.
    Task<IReadOnlyDictionary<string, RoomSnapshot>> FetchAllAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support bulk fetching.");
}
