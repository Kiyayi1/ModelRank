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
    private readonly IWarehouseService _warehouseService;
    private readonly IBrowserService _browserService;

    // Keyed by (site, normalized model name) so each tracked person gets an independent state,
    // browser page, and monitoring loop — this is what makes concurrent tracking possible.
    private readonly ConcurrentDictionary<(Site Site, string Key), SiteMonitorState> _states = new();
    private readonly ConcurrentDictionary<(Site Site, string Key), SemaphoreSlim> _monitoringLocks = new();

    // For a bulk-capable scraper (see ISiteScraper.SupportsBulkFetch), all trackers on a site
    // share ONE poll loop keyed by site only — one fetch updates every tracked model on that
    // site at once, instead of each model triggering its own independent fetch. Presence of a
    // site's key here means its shared loop is currently running.
    private readonly ConcurrentDictionary<Site, CancellationTokenSource> _bulkLoopTokens = new();
    private readonly object _bulkLoopStartLock = new();

    public event Action<Site, string>? StateChanged;

    public MonitoringService(ISiteScraperFactory scraperFactory, IStorageService storageService, IWarehouseService warehouseService, IBrowserService browserService)
    {
        _scraperFactory = scraperFactory;
        _storageService = storageService;
        _warehouseService = warehouseService;
        _browserService = browserService;
    }

    private static string NormalizeKey(string modelName) => modelName.Trim().ToLowerInvariant();

    public IReadOnlyList<SiteMonitorState> GetStates(Site site) =>
        _states.Where(kv => kv.Key.Site == site)
               .Select(kv => kv.Value)
               .OrderBy(s => s.ModelName, StringComparer.OrdinalIgnoreCase)
               .ToList();

    public async Task StartMonitoringAsync(Site site, string modelName, double intervalMinutes)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        var key = NormalizeKey(modelName);
        var stateKey = (site, key);
        var state = _states.GetOrAdd(stateKey, _ => new SiteMonitorState { ModelName = modelName });
        if (state.IsMonitoring) return;

        var scraper = _scraperFactory.GetScraper(site);

        var lockObj = _monitoringLocks.GetOrAdd(stateKey, _ => new SemaphoreSlim(1, 1));
        if (!await lockObj.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            state.StatusMessage = "Cannot start: previous session is still shutting down.";
            StateChanged?.Invoke(site, key);
            return;
        }

        try
        {
            if (state.IsMonitoring) return;

            if (state.Results.Count == 0)
            {
                var recent = await _storageService.GetResultsForModelAsync(site, modelName, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);
                state.Results.AddRange(recent);
                state.Results = state.Results.OrderBy(r => r.Timestamp).ToList();
            }

            state.ModelName = modelName;
            state.IntervalMinutes = intervalMinutes;
            state.IsMonitoring = true;
            state.NextSearchTime = DateTime.UtcNow;
            state.StatusMessage = $"Starting monitoring for '{modelName}'...";
            StateChanged?.Invoke(site, key);

            if (scraper.SupportsBulkFetch)
            {
                // No dedicated loop/CancellationTokenSource for this tracker — it rides the
                // site's shared bulk loop, started (if not already running) below.
                EnsureBulkLoopRunning(site, scraper);
            }
            else
            {
                state.CancellationTokenSource = new CancellationTokenSource();
                _ = RunMonitoringLoopAsync(site, key, state.CancellationTokenSource.Token);
            }
        }
        finally
        {
            lockObj.Release();
        }
    }

    public void StopMonitoring(Site site, string modelName)
    {
        var key = NormalizeKey(modelName);
        if (_states.TryGetValue((site, key), out var state))
        {
            state.IsMonitoring = false;
            state.StatusMessage = "Monitoring stopped.";
            StateChanged?.Invoke(site, key);

            var scraper = _scraperFactory.GetScraper(site);
            if (scraper.SupportsBulkFetch)
            {
                // Stop the shared loop only once nobody on this site needs it anymore; it
                // restarts automatically the next time any tracker on this site is started.
                if (!GetStates(site).Any(s => s.IsMonitoring) && _bulkLoopTokens.TryRemove(site, out var cts))
                {
                    cts.Cancel();
                }
            }
            else
            {
                state.CancellationTokenSource?.Cancel();
                _ = _browserService.ClosePageAsync(site, key);
            }
        }
    }

    private void EnsureBulkLoopRunning(Site site, ISiteScraper scraper)
    {
        lock (_bulkLoopStartLock)
        {
            if (_bulkLoopTokens.ContainsKey(site)) return;
            var cts = new CancellationTokenSource();
            _bulkLoopTokens[site] = cts;
            _ = RunBulkLoopAsync(site, scraper, cts.Token);
        }
    }

    // Shared per-site poll loop for bulk-capable scrapers: one fetch per cycle updates every
    // currently-monitoring tracker on that site, instead of one fetch per model. The cycle
    // interval is the shortest interval configured across those trackers, so nobody waits
    // longer than they asked for.
    private async Task RunBulkLoopAsync(Site site, ISiteScraper scraper, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var activeTrackers = GetStates(site).Where(s => s.IsMonitoring).ToList();
            if (activeTrackers.Count == 0)
            {
                _bulkLoopTokens.TryRemove(site, out _);
                return;
            }

            int intervalMs = (int)(activeTrackers.Min(s => s.IntervalMinutes > 0 ? s.IntervalMinutes : 5) * 60 * 1000);

            void NotifyAll()
            {
                foreach (var t in activeTrackers) StateChanged?.Invoke(site, NormalizeKey(t.ModelName));
            }

            foreach (var t in activeTrackers)
            {
                t.IsSearching = true;
                t.StatusMessage = $"Searching {site} for '{t.ModelName}'...";
                t.NextSearchTime = DateTime.UtcNow.AddMilliseconds(intervalMs);
            }
            NotifyAll();

            var progress = new Progress<string>(msg =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var t in activeTrackers) t.StatusMessage = msg;
                NotifyAll();
            });

            IReadOnlyDictionary<string, RoomSnapshot>? snapshot = null;
            try
            {
                snapshot = await scraper.FetchAllAsync(progress, token);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) break;
            }
            catch (Exception ex)
            {
                foreach (var t in activeTrackers)
                    t.StatusMessage = $"Error: {ex.Message}. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                Debug.WriteLine($"[MonitoringService] Bulk fetch exception: {ex}");
            }

            if (snapshot != null)
            {
                foreach (var tracker in activeTrackers)
                {
                    if (snapshot.TryGetValue(tracker.ModelName, out var hit))
                    {
                        var previous = tracker.Results.LastOrDefault();
                        var result = BuildSearchResult(site, tracker.ModelName, hit);
                        ApplyDeltas(result, previous);

                        await _storageService.SaveResultAsync(result);
                        await _warehouseService.SaveResultAsync(result);

                        tracker.Results.Add(result);
                        tracker.StatusMessage = $"Found at {result.Timestamp:HH:mm:ss}";
                    }
                    else
                    {
                        tracker.StatusMessage = $"No result this time. Next check in {FormatTimeSpan(TimeSpan.FromMilliseconds(intervalMs))}.";
                    }
                }
            }

            foreach (var t in activeTrackers) t.IsSearching = false;
            NotifyAll();

            if (token.IsCancellationRequested) break;

            var waitEnd = DateTime.UtcNow.AddMilliseconds(intervalMs);
            while (DateTime.UtcNow < waitEnd && !token.IsCancellationRequested)
            {
                var remaining = waitEnd - DateTime.UtcNow;
                var stillActive = GetStates(site).Where(s => s.IsMonitoring).ToList();
                if (stillActive.Count == 0) break; // everyone stopped mid-wait; loop back around to exit cleanly
                foreach (var t in stillActive) t.StatusMessage = $"Next search in {FormatTimeSpan(remaining)}";
                foreach (var t in stillActive) StateChanged?.Invoke(site, NormalizeKey(t.ModelName));
                await Task.Delay(1000, token);
            }
        }

        _bulkLoopTokens.TryRemove(site, out _);
    }

    private static SearchResult BuildSearchResult(Site site, string modelName, RoomSnapshot hit) => new()
    {
        Timestamp = DateTime.Now,
        Site = site,
        ModelName = modelName,
        DisplayName = hit.Username,
        Page = hit.Page,
        Position = hit.Position,
        Rank = hit.Rank,
        Viewers = hit.Viewers.ToString(),
        Followers = (int)hit.Followers,
        Tags = hit.Tags,
        ImageUrl = hit.ImageUrl,
        IsNew = hit.IsNew,
        SessionStartUtc = hit.StartTimestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(hit.StartTimestamp).UtcDateTime : null,
        Label = hit.Label,
        HasPassword = hit.HasPassword,
        TokensRemaining = hit.TokensRemaining,
        Found = true
    };

    private void ApplyDeltas(SearchResult result, SearchResult? previous)
    {
        if (previous == null) return;

        result.RankChange = previous.Rank - result.Rank;
        result.PageChange = result.Page - previous.Page;
        result.PositionChange = result.Position - previous.Position;
        result.ViewersChange = ParseViewers(result.Viewers) - ParseViewers(previous.Viewers);

        // Tokens tipped this period = how much the goal counter dropped since the last poll. A
        // negative delta means a new goal started (or was reset) between polls, which isn't a
        // real "tip count" — leave it unknown rather than show a negative.
        if (previous.TokensRemaining.HasValue && result.TokensRemaining.HasValue)
        {
            var delta = previous.TokensRemaining.Value - result.TokensRemaining.Value;
            result.TokensTippedThisPeriod = delta >= 0 ? delta : null;
        }
    }

    public void RemoveTracker(Site site, string modelName)
    {
        var key = NormalizeKey(modelName);
        var stateKey = (site, key);
        if (_states.TryGetValue(stateKey, out var state) && !state.IsMonitoring)
        {
            _states.TryRemove(stateKey, out _);
            StateChanged?.Invoke(site, key);
        }
    }

    private async Task RunMonitoringLoopAsync(Site site, string key, CancellationToken token)
    {
        if (!_states.TryGetValue((site, key), out var state)) return;
        var scraper = _scraperFactory.GetScraper(site);
        SearchResult? previous = state.Results.LastOrDefault();
        int intervalMs = (int)(state.IntervalMinutes * 60 * 1000);

        IPage? page = scraper.RequiresBrowser ? await _browserService.GetOrCreatePageAsync(site, key) : null;

        var progress = new Progress<string>(msg =>
        {
            if (!token.IsCancellationRequested)
            {
                state.StatusMessage = msg;
                StateChanged?.Invoke(site, key);
            }
        });

        while (!token.IsCancellationRequested)
        {
            state.IsSearching = true;
            state.StatusMessage = $"Searching {site} for '{state.ModelName}'...";
            state.NextSearchTime = DateTime.UtcNow.AddMilliseconds(intervalMs);
            StateChanged?.Invoke(site, key);

            try
            {
                var output = await scraper.FindModelRankAsync(page, state.ModelName, progress, token);
                if (token.IsCancellationRequested) break;

                var result = ParseResult(output);
                if (result != null)
                {
                    result.Site = site;
                    result.ModelName = state.ModelName;
                    ApplyDeltas(result, previous);

                    await _storageService.SaveResultAsync(result);
                    await _warehouseService.SaveResultAsync(result); // fire-and-await, never blocks the UI on failure (see SqliteWarehouseService)

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
                StateChanged?.Invoke(site, key);
            }

            if (token.IsCancellationRequested) break;

            // Countdown loop – only if we didn't continue (i.e., model not found but last page not reached)
            var waitEnd = DateTime.UtcNow.AddMilliseconds(intervalMs);
            while (DateTime.UtcNow < waitEnd && !token.IsCancellationRequested)
            {
                var remaining = waitEnd - DateTime.UtcNow;
                state.StatusMessage = $"Next search in {FormatTimeSpan(remaining)}";
                StateChanged?.Invoke(site, key);
                await Task.Delay(1000, token);
            }
        }

        state.IsMonitoring = false;
        state.IsSearching = false;
        StateChanged?.Invoke(site, key);
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

        var regex = new Regex(@"Found '(.*?)'(?: \(display: (.*?)\))? on page (\d+), position (\d+) \(overall rank: (\d+)\) \| Viewers: ([^|]+)(?: \| Followers: (\d+))?(?: \| Tags: ([^|]*))?(?: \| Img: ([^|]*))?(?: \| IsNew: (\S+))?(?: \| StartTs: (\d+))?(?: \| Label: ([^|]*))?(?: \| HasPassword: (\S+))?(?: \| TokensRemaining: (\d+))?$");
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
            Followers = match.Groups[7].Success ? int.Parse(match.Groups[7].Value) : 0,
            Tags = match.Groups[8].Success
                ? match.Groups[8].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : new List<string>(),
            ImageUrl = match.Groups[9].Success ? match.Groups[9].Value.Trim() : "",
            IsNew = match.Groups[10].Success && bool.TryParse(match.Groups[10].Value, out var isNew) && isNew,
            SessionStartUtc = match.Groups[11].Success && long.TryParse(match.Groups[11].Value, out var startTs)
                ? DateTimeOffset.FromUnixTimeSeconds(startTs).UtcDateTime
                : null,
            Label = match.Groups[12].Success ? match.Groups[12].Value.Trim() : "",
            HasPassword = match.Groups[13].Success && bool.TryParse(match.Groups[13].Value, out var hasPw) && hasPw,
            TokensRemaining = match.Groups[14].Success ? int.Parse(match.Groups[14].Value) : null,
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
