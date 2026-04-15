using ModelRank.Models;

namespace ModelRank.Services;

public interface ISiteScraperFactory
{
    ISiteScraper GetScraper(Site site);
}