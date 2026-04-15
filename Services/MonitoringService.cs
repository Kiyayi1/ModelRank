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
    private readonly TorProxyManager _torProxyManager;
    private readonly ConcurrentDictionary<Site, SiteMonitorState> _states = new();
    private readonly ConcurrentDictionary<Site, SemaphoreSlim> _monitoringLocks = new();

    private bool _useParallelSearch;
    public bool UseParallelSearch
    {
        get => _useParallelSearch;
        set
        {
            _useParallelSearch = value;
            Debug.WriteLine($"UseParallelSearch set to {value}");
        }
    }

    public bool UseTor { get; set; } = false;

    public event Action<Site>? StateChanged;

    public MonitoringService(
        ISiteScraperFactory scraperFactory,
        IStorageService storageService,
        IBrowserService browserService,
        TorProxyManager torProxyManager)
    {
        _scraperFactory = scraperFactory;
        _storageService = storageService;
        _browserService = browserService;
        _torProxyManager = torProxyManager;

        Debug.WriteLine($"MonitoringService created: {GetHashCode()}");
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
            _ = _browserService.ResetAsync();
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

        bool useParallel = UseParallelSearch && site == Site.Chaturbate;
        Debug.WriteLine($"[Parallel] useParallel = {useParallel} (UseParallelSearch={UseParallelSearch}, site={site})");

        while (!token.IsCancellationRequested)
        {
            state.IsSearching = true;
            state.StatusMessage = $"Searching {site} for '{state.ModelName}'...";
            state.NextSearchTime = DateTime.UtcNow.AddMilliseconds(intervalMs);
            StateChanged?.Invoke(site);

            try
            {
                List<string> output;
                if (useParallel)
                {
                    Debug.WriteLine("[Parallel] Entering parallel branch.");
                    var segmentStatuses = new ConcurrentDictionary<int, string>();
                    void UpdateSegmentStatus(int segmentIndex, string message)
                    {
                        segmentStatuses[segmentIndex] = message;
                        var combined = string.Join("\n", segmentStatuses.OrderBy(kv => kv.Key)
                            .Select(kv => $"[Segment {kv.Key}] {kv.Value}"));
                        state.StatusMessage = combined;
                        StateChanged?.Invoke(site);
                        Debug.WriteLine($"[Parallel] Segment {segmentIndex}: {message}");
                    }

                    if (scraper is ChaturbateScraper chaturbateScraper)
                    {
                        await chaturbateScraper.EnsureConsentAsync(page, token);
                    }

                    int? totalPages = await GetTotalPagesAsync(page);
                    Debug.WriteLine($"[Parallel] Total pages detected: {totalPages}");
                    if (!totalPages.HasValue || totalPages.Value <= 1)
                    {
                        totalPages = 100; // fallback for testing
                        Debug.WriteLine("[Parallel] Using fallback total pages = 100");
                    }

                    int segmentSize = 20;
                    var ranges = Enumerable.Range(1, totalPages.Value)
                        .Select((x, i) => new { Index = i, Value = x })
                        .GroupBy(x => x.Index / segmentSize)
                        .Select(g => (Start: g.First().Value, End: g.Last().Value))
                        .ToList();

                    Debug.WriteLine($"[Parallel] Created {ranges.Count} ranges: {string.Join(", ", ranges.Select(r => $"{r.Start}-{r.End}"))}");

                    string? proxyUrl = null;
                    if (UseTor)
                    {
                        await _torProxyManager.StartAsync();
                        proxyUrl = _torProxyManager.GetProxyUrl(0);
                    }

                    if (scraper is ChaturbateScraper chaturbateScraper2)
                    {
                        output = await chaturbateScraper2.ScanPagesInParallelAsync(ranges, state.ModelName, page, proxyUrl, UpdateSegmentStatus, token);
                    }
                    else
                    {
                        output = await scraper.FindModelRankAsync(page, state.ModelName, progress, token);
                    }
                }
                else
                {
                    output = await scraper.FindModelRankAsync(page, state.ModelName, progress, token);
                }

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
                    state.StatusMessage = $"No result this time. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                }
            }
            catch (OperationCanceledException)
            {
                // User pressed Stop – exit immediately
                state.StatusMessage = "Monitoring stopped.";
                break;
            }
            catch (Exception ex) when (ex.GetType().Name == "TargetClosedException" || ex.Message.Contains("Target page") || ex.Message.Contains("closed"))
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

    private async Task<int?> GetTotalPagesAsync(IPage page)
    {
        try
        {
            // Ensure we are on page 1
            if (!page.Url.Contains("?page=1"))
                await page.GotoAsync("https://chaturbate.com/?page=1", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // Get all page number buttons and find the maximum
            var pageLinks = await page.QuerySelectorAllAsync("a[data-testid='page-number-button']");
            int maxPage = 0;
            foreach (var link in pageLinks)
            {
                var text = await link.TextContentAsync();
                if (int.TryParse(text, out int num) && num > maxPage)
                    maxPage = num;
            }
            return maxPage > 0 ? maxPage : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GetTotalPagesAsync] Error: {ex}");
            return null;
        }
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