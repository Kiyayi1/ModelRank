using Microsoft.Playwright;

namespace ModelRank.Services;

public interface ISiteScraper
{
    Task<List<string>> FindModelRankAsync(IPage page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}