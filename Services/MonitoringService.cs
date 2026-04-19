using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ModelRank.Models;

namespace ModelRank.Services;

public class MonitoringService : IMonitoringService
{
    private readonly ISiteScraperFactory _scraperFactory;
    private readonly IStorageService _storageService;
    private readonly IBrowserService _browserService;
    private readonly ConcurrentDictionary<Site, SiteMonitorState> _states = new();
    private readonly ConcurrentDictionary<Site, SemaphoreSlim> _monitoringLocks = new();

    public event Action<Site>? StateChanged;

    public MonitoringService(ISiteScraperFactory scraperFactory, IStorageService storageService, IBrowserService browserService)
    {
        _scraperFactory = scraperFactory;
        _storageService = storageService;
        _browserService = browserService;
    }

    public SiteMonitorState GetState(Site site) => _states.GetOrAdd(site, _ => new SiteMonitorState());

    public async Task StartMonitoringAsync(Site site, string modelName, double intervalMinutes)
    {
        var state = GetState(site);
        if (state.IsMonitoring) return;

        var lockObj = _monitoringLocks.GetOrAdd(site, _ => new SemaphoreSlim(1, 1));
        if (!await lockObj.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            state.StatusMessage = "Cannot start: previous session is still shutting down.";
            StateChanged?.Invoke(site);
            return;
        }

        try
        {
            if (state.IsMonitoring) return;

            if (!string.Equals(state.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                state.Results.Clear();
                state.ModelName = modelName;
                var recent = await _storageService.GetResultsForModelAsync(site, modelName, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);
                state.Results.AddRange(recent);
                state.Results = state.Results.OrderBy(r => r.Timestamp).ToList();
                StateChanged?.Invoke(site);
            }

            state.IntervalMinutes = intervalMinutes;
            state.IsMonitoring = true;
            state.CancellationTokenSource = new CancellationTokenSource();
            state.NextSearchTime = DateTime.UtcNow;
            state.StatusMessage = $"Starting monitoring for '{modelName}'...";
            StateChanged?.Invoke(site);

            _ = RunMonitoringLoopAsync(site, state.CancellationTokenSource.Token);
        }
        finally
        {
            lockObj.Release();
        }
    }

    public void StopMonitoring(Site site)
    {
        if (_states.TryGetValue(site, out var state))
        {
            state.CancellationTokenSource?.Cancel();
            state.IsMonitoring = false;
            state.StatusMessage = "Monitoring stopped.";
            StateChanged?.Invoke(site);
            _ = _browserService.ClosePageAsync(site);
        }
    }

    private async Task RunMonitoringLoopAsync(Site site, CancellationToken token)
    {
        var state = GetState(site);
        var scraper = _scraperFactory.GetScraper(site);
        SearchResult? previous = state.Results.LastOrDefault();
        int intervalMs = (int)(state.IntervalMinutes * 60 * 1000);

        var page = await _browserService.GetOrCreatePageAsync(site);

        var progress = new Progress<string>(msg =>
        {
            if (!token.IsCancellationRequested)
            {
                state.StatusMessage = msg;
                StateChanged?.Invoke(site);
            }
        });

        while (!token.IsCancellationRequested)
        {
            state.IsSearching = true;
            state.StatusMessage = $"Searching {site} for '{state.ModelName}'...";
            state.NextSearchTime = DateTime.UtcNow.AddMilliseconds(intervalMs);
            StateChanged?.Invoke(site);

            try
            {
                var output = await scraper.FindModelRankAsync(page, state.ModelName, progress, token);
                if (token.IsCancellationRequested) break;

                var result = ParseResult(output);
                if (result != null)
                {
                    result.Site = site;
                    result.ModelName = state.ModelName;
                    await _storageService.SaveResultAsync(result);

                    if (previous != null)
                    {
                        result.RankChange = previous.Rank - result.Rank;
                        result.PageChange = result.Page - previous.Page;
                        result.PositionChange = result.Position - previous.Position;
                        result.ViewersChange = ParseViewers(result.Viewers) - ParseViewers(previous.Viewers);
                    }

                    state.Results.Add(result);
                    previous = result;
                    state.StatusMessage = $"Found at {result.Timestamp:HH:mm:ss}";
                }
                else
                {
                    bool lastPageReached = output.Contains("LAST_PAGE_REACHED");
                    if (lastPageReached)
                    {
                        state.StatusMessage = $"Model not found after scanning all pages. Restarting immediately from page 1.";
                        continue; // skip countdown
                    }
                    else
                    {
                        state.StatusMessage = $"No result this time. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    state.StatusMessage = "Monitoring stopped.";
                    break;
                }
                state.StatusMessage = $"Search cancelled. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
            }
            catch (Exception ex) when (ex.GetType().Name == "TargetClosedException")
            {
                if (!token.IsCancellationRequested)
                    state.StatusMessage = $"Browser closed. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                else
                    state.StatusMessage = "Monitoring stopped.";
            }
            catch (Exception ex)
            {
                state.StatusMessage = $"Error: {ex.Message}. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                Debug.WriteLine($"[MonitoringService] Exception: {ex}");
            }
            finally
            {
                state.IsSearching = false;
                state.NextSearchTime = DateTime.UtcNow.AddMilliseconds(intervalMs);
                StateChanged?.Invoke(site);
            }

            if (token.IsCancellationRequested) break;

            // Countdown loop – only if we didn't continue (i.e., model not found but last page not reached)
            var waitEnd = DateTime.UtcNow.AddMilliseconds(intervalMs);
            while (DateTime.UtcNow < waitEnd && !token.IsCancellationRequested)
            {
                var remaining = waitEnd - DateTime.UtcNow;
                state.StatusMessage = $"Next search in {FormatTimeSpan(remaining)}";
                StateChanged?.Invoke(site);
                await Task.Delay(1000, token);
            }
        }

        state.IsMonitoring = false;
        state.IsSearching = false;
        StateChanged?.Invoke(site);
    }

    private string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.Seconds}s";
        if (ts.TotalMinutes < 60) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Hours}h {ts.Minutes}m";
    }

    private SearchResult? ParseResult(List<string> output)
    {
        var foundLine = output.FirstOrDefault(l => l.Contains("Found") && l.Contains("page") && l.Contains("rank"));
        if (foundLine == null) return null;

        var regex = new Regex(@"Found '(.*?)'(?: \(display: (.*?)\))? on page (\d+), position (\d+) \(overall rank: (\d+)\) \| Viewers: (.*)");
        var match = regex.Match(foundLine);
        if (!match.Success) return null;

        return new SearchResult
        {
            Timestamp = DateTime.Now,
            ModelName = match.Groups[1].Value,
            DisplayName = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value,
            Page = int.Parse(match.Groups[3].Value),
            Position = int.Parse(match.Groups[4].Value),
            Rank = int.Parse(match.Groups[5].Value),
            Viewers = match.Groups[6].Value.Trim(),
            Found = true
        };
    }

    private int ParseViewers(string viewersText)
    {
        if (string.IsNullOrWhiteSpace(viewersText)) return 0;
        var cleaned = Regex.Replace(viewersText, @"[^0-9\.km]", "", RegexOptions.IgnoreCase);
        double multiplier = 1;
        if (cleaned.EndsWith("k", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1000;
            cleaned = cleaned[..^1];
        }
        else if (cleaned.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1000000;
            cleaned = cleaned[..^1];
        }
        if (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            return (int)(val * multiplier);
        return 0;
    }
}