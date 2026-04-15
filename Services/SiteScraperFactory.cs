using ModelRank.Models;

namespace ModelRank.Services;

public class SiteScraperFactory : ISiteScraperFactory
{
    private readonly ChaturbateScraper _chaturbate;
    private readonly CamsodaScraper _camsoda;
    private readonly Cam4Scraper _cam4;

    public SiteScraperFactory(ChaturbateScraper chaturbate, CamsodaScraper camsoda, Cam4Scraper cam4)
    {
        _chaturbate = chaturbate;
        _camsoda = camsoda;
        _cam4 = cam4;
    }

    public ISiteScraper GetScraper(Site site) => site switch
    {
        Site.Chaturbate => _chaturbate,
        Site.Camsoda => _camsoda,
        Site.Cam4 => _cam4,
        _ => throw new NotSupportedException($"Site {site} not supported")
    };
}