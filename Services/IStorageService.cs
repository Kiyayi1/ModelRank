using ModelRank.Models;

namespace ModelRank.Services;

public interface IStorageService
{
    Task SaveResultAsync(SearchResult result);
    Task<List<string>> GetDistinctModelNamesAsync(Site site);
    Task<(DateTime Min, DateTime Max)> GetTimeRangeForModelAsync(Site site, string modelName);
    Task<List<SearchResult>> GetResultsForModelAsync(Site site, string modelName, DateTime? from = null, DateTime? to = null);
    Task ExportToCsvAsync(Site site, string modelName, DateTime from, DateTime to, string filePath);
    Task DeleteResultAsync(int id);
    Task DeleteResultsAsync(IEnumerable<int> ids);
}