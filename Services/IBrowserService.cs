using Microsoft.Playwright;
using ModelRank.Models;

namespace ModelRank.Services;

public interface IBrowserService : IAsyncDisposable
{
    Task<IPage> GetOrCreatePageAsync(Site site, IProgress<string>? progress = null);
    Task ClosePageAsync(Site site);
    Task ResetAsync();
}
