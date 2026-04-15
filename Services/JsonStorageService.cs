using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelRank.Models;

namespace ModelRank.Services;

public class JsonStorageService : IStorageService
{
    private readonly string _filePath;
    private List<SearchResult> _allResults = new();

    public JsonStorageService()
    {
        var appDir = AppContext.BaseDirectory;
        _filePath = Path.Combine(appDir, "search_history.json");
        Directory.CreateDirectory(appDir);
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _allResults = JsonSerializer.Deserialize<List<SearchResult>>(json) ?? new List<SearchResult>();
            }
            else
            {
                _allResults = new List<SearchResult>();
            }
        }
        catch (JsonException)
        {
            // File is corrupted – delete it and start fresh
            File.Delete(_filePath);
            _allResults = new List<SearchResult>();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_allResults, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public Task SaveResultAsync(SearchResult result)
    {
        result.Id = _allResults.Any() ? _allResults.Max(r => r.Id) + 1 : 1;
        _allResults.Add(result);
        Save();
        return Task.CompletedTask;
    }

    public Task<List<string>> GetDistinctModelNamesAsync(Site site)
    {
        var names = _allResults.Where(r => r.Site == site).Select(r => r.ModelName).Distinct().OrderBy(n => n).ToList();
        return Task.FromResult(names);
    }

    public Task<(DateTime Min, DateTime Max)> GetTimeRangeForModelAsync(Site site, string modelName)
    {
        var results = _allResults.Where(r => r.Site == site && r.ModelName == modelName).ToList();
        if (!results.Any())
            return Task.FromResult((DateTime.MinValue, DateTime.MaxValue));
        var min = results.Min(r => r.Timestamp);
        var max = results.Max(r => r.Timestamp);
        return Task.FromResult((min, max));
    }

    public Task<List<SearchResult>> GetResultsForModelAsync(Site site, string modelName, DateTime? from = null, DateTime? to = null)
    {
        var query = _allResults.Where(r => r.Site == site && r.ModelName == modelName);
        if (from.HasValue)
            query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.Timestamp <= to.Value);
        var results = query.OrderBy(r => r.Timestamp).ToList();
        return Task.FromResult(results);
    }

    public Task ExportToCsvAsync(Site site, string modelName, DateTime from, DateTime to, string filePath)
    {
        var results = _allResults.Where(r => r.Site == site && r.ModelName == modelName && r.Timestamp >= from && r.Timestamp <= to)
                                 .OrderBy(r => r.Timestamp)
                                 .ToList();
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("Timestamp,Page,Position,Rank,Viewers");
        foreach (var r in results)
        {
            writer.WriteLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.Page},{r.Position},{r.Rank},{r.Viewers}");
        }
        return Task.CompletedTask;
    }

    public Task DeleteResultAsync(int id)
    {
        var result = _allResults.FirstOrDefault(r => r.Id == id);
        if (result != null)
        {
            _allResults.Remove(result);
            Save();
        }
        return Task.CompletedTask;
    }

    public Task DeleteResultsAsync(IEnumerable<int> ids)
    {
        var idsToRemove = new HashSet<int>(ids);
        _allResults.RemoveAll(r => idsToRemove.Contains(r.Id));
        Save();
        return Task.CompletedTask;
    }
}