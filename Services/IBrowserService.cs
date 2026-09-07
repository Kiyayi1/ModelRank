using Microsoft.Playwright;
using ModelRank.Models;

namespace ModelRank.Services;

public interface IBrowserService : IAsyncDisposable
{
    // trackerKey identifies the individual model being monitored (e.g. its normalized name) so
    // that multiple models on the same site each get their own Playwright page and can be
    // scraped concurrently without stepping on each other's navigation.
    Task<IPage> GetOrCreatePageAsync(Site site, string trackerKey, IProgress<string>? progress = null);
    Task ClosePageAsync(Site site, string trackerKey);
    Task ResetAsync();
}
