using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelRank.Models;

namespace ModelRank.Services;

public class ChaturbateScraper : ISiteScraper
{
    private static readonly Random _random = new Random();
    private static readonly HashSet<Site> _consentHandled = new HashSet<Site>();
    private static readonly object _consentLock = new object();

    private static readonly string[] _userAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/119.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:108.0) Gecko/20100101 Firefox/108.0"
    };

    // =========================================================================
    // Sequential search
    // =========================================================================
    public async Task<List<string>> FindModelRankAsync(IPage page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        UiLog($"Starting new search for '{modelName}' from page 1.", output, progress);
        Debug.WriteLine($"[Chaturbate] Starting search for {modelName}");

        page.SetDefaultTimeout(300_000); // 5 minutes

        int pageNum = 1;
        bool found = false;
        int globalCount = 0;
        const int maxRetries = 3;

        try
        {
            while (!found && !cancellationToken.IsCancellationRequested)
            {
                string url = $"https://chaturbate.com/?page={pageNum}";
                UiLog($"Scanning page {pageNum}...", output, progress);
                Debug.WriteLine($"[Chaturbate] Scanning page {pageNum}");

                bool pageProcessed = false;
                int retryCount = 0;

                while (!pageProcessed && !cancellationToken.IsCancellationRequested && retryCount < maxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var userAgent = _userAgents[_random.Next(_userAgents.Length)];
                    await page.Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { { "User-Agent", userAgent } });
                    Debug.WriteLine($"[Chaturbate] Using user agent: {userAgent}");

                    await Task.Delay(_random.Next(2000, 5000), cancellationToken);
                    Debug.WriteLine($"[Chaturbate] Navigating to {url}");
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    Debug.WriteLine("[Chaturbate] Navigation completed, waiting for DOM content");
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    Debug.WriteLine("[Chaturbate] DOM ready");

                    var title = await page.TitleAsync();
                    Debug.WriteLine($"[Chaturbate] Page title: {title}");

                    // Cloudflare challenge detection
                    if (title.Contains("Just a moment") || title.Contains("security verification") || title.Contains("Cloudflare"))
                    {
                        retryCount++;
                        int waitSeconds = (int)Math.Pow(2, retryCount);
                        Debug.WriteLine($"[Chaturbate] Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s (retry {retryCount}/{maxRetries})...");
                        UiLog($"Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s...", output, progress);
                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(waitSeconds * 1000, cancellationToken);
                            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                            continue;
                        }
                        else
                        {
                            UiLog($"Cloudflare challenge persisted. Moving to next page.", output, progress);
                            pageProcessed = true;
                            break;
                        }
                    }

                    // Consent on first page
                    if (pageNum == 1 && !_consentHandled.Contains(Site.Chaturbate))
                    {
                        Debug.WriteLine("[Chaturbate] Checking consent button");
                        bool consentClicked = false;

                        try
                        {
                            var agreeButton = await page.WaitForSelectorAsync("a#close_entrance_terms", new PageWaitForSelectorOptions { Timeout = 10000 });
                            if (agreeButton != null)
                            {
                                Debug.WriteLine("[Chaturbate] Consent button found, clicking");
                                await agreeButton.ClickAsync();
                                await Task.Delay(1000, cancellationToken);
                                consentClicked = true;
                                UiLog("Consent accepted (normal click).", output, progress);
                            }
                        }
                        catch (TimeoutException)
                        {
                            Debug.WriteLine("[Chaturbate] Consent button not found, trying JS fallback");
                        }

                        if (!consentClicked)
                        {
                            try
                            {
                                var result = await page.EvaluateAsync<bool>(@"() => {
                                    const btn = document.querySelector('a#close_entrance_terms');
                                    if(btn) { btn.click(); return true; }
                                    return false;
                                }");
                                if (result)
                                {
                                    Debug.WriteLine("[Chaturbate] Consent clicked via JavaScript");
                                    await Task.Delay(1000, cancellationToken);
                                    consentClicked = true;
                                    UiLog("Consent accepted (JS fallback).", output, progress);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Chaturbate] JS consent click failed: {ex.Message}");
                            }
                        }

                        lock (_consentLock) _consentHandled.Add(Site.Chaturbate);
                    }

                    // Wait for username links
                    IReadOnlyList<IElementHandle>? usernameLinks = null;
                    try
                    {
                        await page.WaitForSelectorAsync("a[data-testid='room-card-username']", new PageWaitForSelectorOptions { Timeout = 60000 });
                        usernameLinks = await page.QuerySelectorAllAsync("a[data-testid='room-card-username']");
                    }
                    catch (TimeoutException)
                    {
                        retryCount++;
                        UiLog($"Timeout waiting for cards on page {pageNum} (retry {retryCount}/{maxRetries}). Refreshing...", output, progress);
                        if (retryCount < maxRetries) continue;
                        else
                        {
                            UiLog($"Max retries reached. Moving to next page.", output, progress);
                            pageProcessed = true;
                            break;
                        }
                    }

                    if (usernameLinks == null || usernameLinks.Count == 0)
                    {
                        retryCount++;
                        UiLog($"No cards on page {pageNum} (retry {retryCount}/{maxRetries}). Refreshing...", output, progress);
                        if (retryCount < maxRetries) continue;
                        else
                        {
                            UiLog($"Max retries reached. Moving to next page.", output, progress);
                            pageProcessed = true;
                            break;
                        }
                    }

                    pageProcessed = true;
                    UiLog($"Page {pageNum}: found {usernameLinks.Count} models.", output, progress);

                    // Sample first 5 usernames
                    var sampleTasks = usernameLinks.Take(5).Select(async link =>
                    {
                        var username = await link.TextContentAsync();
                        return username?.Trim() ?? "empty";
                    });
                    var samples = await Task.WhenAll(sampleTasks);
                    output.Add($"Sample usernames: {string.Join(", ", samples)}");
                    Debug.WriteLine($"[Chaturbate] Sample usernames: {string.Join(", ", samples)}");

                    // Process each card
                    for (int i = 0; i < usernameLinks.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var link = usernameLinks[i];
                        var username = await link.TextContentAsync();
                        username = username?.Trim().ToLower() ?? "";

                        // --- Walk up the DOM tree to find container with viewers ---
                        IElementHandle? card = link;
                        for (int level = 0; level < 5; level++)
                        {
                            var parentHandle = await card.EvaluateHandleAsync("el => el.parentElement");
                            if (parentHandle == null) break;
                            var parent = parentHandle as IElementHandle;
                            if (parent == null) break;
                            card = parent;
                            var testViewers = await card.QuerySelectorAsync(".viewers, .sub-info .viewers, li.cams .viewers");
                            if (testViewers != null) break;
                        }

                        string viewers = "N/A";
                        var viewersElement = await card.QuerySelectorAsync(".viewers, .sub-info .viewers, li.cams .viewers");
                        if (viewersElement != null)
                        {
                            var viewersText = await viewersElement.TextContentAsync();
                            viewers = viewersText?.Trim() ?? "N/A";
                            var match = Regex.Match(viewers, @"\d+");
                            if (match.Success)
                                viewers = match.Value;
                        }
                        else
                        {
                            Debug.WriteLine("[Chaturbate] Viewers element not found");
                        }

                        if (username.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            int localPos = i + 1;
                            int totalRank = globalCount + localPos;
                            var foundMsg = $"Found '{modelName}' on page {pageNum}, position {localPos} (overall rank: {totalRank}) | Viewers: {viewers}";
                            UiLog(foundMsg, output, progress);
                            Debug.WriteLine($"[Chaturbate] FOUND: {foundMsg}");
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        globalCount += usernameLinks.Count;
                }

                if (!found)
                {
                    pageNum++;
                    await Task.Delay(_random.Next(2000, 5000), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            UiLog("Search cancelled.", output, progress);
            Debug.WriteLine("[Chaturbate] Search cancelled");
        }
        catch (Exception ex)
        {
            UiLog($"Error: {ex.Message}", output, progress);
            Debug.WriteLine($"[Chaturbate] Exception: {ex}");
        }

        return output;
    }

    // =========================================================================
    // Parallel search
    // =========================================================================
    public async Task<List<string>> ScanPagesInParallelAsync(
        IEnumerable<(int start, int end)> ranges,
        string modelName,
        IPage sourcePage,
        string? proxyServer = null,
        Action<int, string>? segmentProgress = null,
        CancellationToken cancellationToken = default)
    {
        var allOutput = new List<string>();
        using var playwright = await Playwright.CreateAsync();

        // Copy cookies from source page (consent already handled)
        var sourceCookies = await sourcePage.Context.CookiesAsync();
        var cookies = sourceCookies.Select(c => new Microsoft.Playwright.Cookie
        {
            Name = c.Name,
            Value = c.Value,
            Domain = c.Domain,
            Path = c.Path,
            Expires = c.Expires,
            HttpOnly = c.HttpOnly,
            Secure = c.Secure,
            SameSite = c.SameSite
        }).ToList();

        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
        });

        var tasks = new List<Task<(List<string> output, int start, int end)>>();
        int segmentIndex = 0;

        foreach (var (start, end) in ranges)
        {
            segmentIndex++;
            string rangeLabel = $"{start}-{end}";
            segmentProgress?.Invoke(segmentIndex, $"Starting range {rangeLabel}");

            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                UserAgent = _userAgents[_random.Next(_userAgents.Length)],
                Locale = "en-US",
                TimezoneId = "Africa/Nairobi",
                ScreenSize = new ScreenSize { Width = 1920, Height = 1080 }
            };
            if (!string.IsNullOrEmpty(proxyServer))
                contextOptions.Proxy = new Proxy { Server = proxyServer };

            var context = await browser.NewContextAsync(contextOptions);
            await context.AddCookiesAsync(cookies);

            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            }");

            var task = ScanPageRangeWithResultAsync(page, start, end, modelName, segmentIndex, rangeLabel, segmentProgress, cancellationToken);
            tasks.Add(task);
        }

        var results = await Task.WhenAll(tasks);
        foreach (var (output, start, end) in results)
        {
            allOutput.AddRange(output);
            segmentProgress?.Invoke(0, $"Segment {start}-{end} completed with {output.Count(o => o.Contains("Found"))} results.");
        }

        return allOutput;
    }

    private async Task<(List<string> output, int start, int end)> ScanPageRangeWithResultAsync(
        IPage page,
        int startPage,
        int endPage,
        string modelName,
        int segmentIndex,
        string rangeLabel,
        Action<int, string>? segmentProgress,
        CancellationToken cancellationToken)
    {
        var output = new List<string>();
        int globalCount = 0;
        const int maxRetries = 3;

        try
        {
            for (int pageNum = startPage; pageNum <= endPage && !cancellationToken.IsCancellationRequested; pageNum++)
            {
                bool pageProcessed = false;
                int retryCount = 0;

                while (!pageProcessed && !cancellationToken.IsCancellationRequested && retryCount < maxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string url = $"https://chaturbate.com/?page={pageNum}";
                    segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Scanning page {pageNum} (attempt {retryCount + 1})...");
                    output.Add($"Scanning page {pageNum}...");

                    await Task.Delay(_random.Next(2000, 5000), cancellationToken);
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                    var title = await page.TitleAsync();
                    if (title.Contains("Just a moment") || title.Contains("security verification") || title.Contains("Cloudflare"))
                    {
                        retryCount++;
                        int waitSeconds = (int)Math.Pow(2, retryCount);
                        segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s (retry {retryCount}/{maxRetries})...");
                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(waitSeconds * 1000, cancellationToken);
                            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                            continue;
                        }
                        else
                        {
                            segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Cloudflare challenge persisted. Skipping page {pageNum}.");
                            pageProcessed = true;
                            break;
                        }
                    }

                    IReadOnlyList<IElementHandle>? usernameLinks = null;
                    try
                    {
                        await page.WaitForSelectorAsync("a[data-testid='room-card-username']", new PageWaitForSelectorOptions { Timeout = 60000 });
                        usernameLinks = await page.QuerySelectorAllAsync("a[data-testid='room-card-username']");
                    }
                    catch (TimeoutException)
                    {
                        retryCount++;
                        segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Timeout waiting for cards on page {pageNum} (retry {retryCount}/{maxRetries})...");
                        continue;
                    }

                    if (usernameLinks == null || usernameLinks.Count == 0)
                    {
                        retryCount++;
                        segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] No cards on page {pageNum} (retry {retryCount}/{maxRetries})...");
                        continue;
                    }

                    pageProcessed = true;
                    segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Page {pageNum}: found {usernameLinks.Count} models.");

                    var sampleTasks = usernameLinks.Take(5).Select(async link =>
                    {
                        var username = await link.TextContentAsync();
                        return username?.Trim() ?? "empty";
                    });
                    var samples = await Task.WhenAll(sampleTasks);
                    output.Add($"Sample usernames: {string.Join(", ", samples)}");

                    for (int i = 0; i < usernameLinks.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var link = usernameLinks[i];
                        var username = await link.TextContentAsync();
                        username = username?.Trim().ToLower() ?? "";

                        // Walk up to find container with viewers
                        IElementHandle? card = link;
                        for (int level = 0; level < 5; level++)
                        {
                            var parentHandle = await card.EvaluateHandleAsync("el => el.parentElement");
                            if (parentHandle == null) break;
                            var parent = parentHandle as IElementHandle;
                            if (parent == null) break;
                            card = parent;
                            var testViewers = await card.QuerySelectorAsync(".viewers, .sub-info .viewers, li.cams .viewers");
                            if (testViewers != null) break;
                        }

                        string viewers = "N/A";
                        var viewersElement = await card.QuerySelectorAsync(".viewers, .sub-info .viewers, li.cams .viewers");
                        if (viewersElement != null)
                        {
                            var viewersText = await viewersElement.TextContentAsync();
                            viewers = viewersText?.Trim() ?? "N/A";
                            var match = Regex.Match(viewers, @"\d+");
                            if (match.Success)
                                viewers = match.Value;
                        }

                        if (username.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            int localPos = i + 1;
                            int totalRank = globalCount + localPos;
                            var foundMsg = $"Found '{modelName}' on page {pageNum}, position {localPos} (overall rank: {totalRank}) | Viewers: {viewers}";
                            output.Add(foundMsg);
                            segmentProgress?.Invoke(segmentIndex, foundMsg);
                            return (output, startPage, endPage);
                        }
                    }

                    if (!pageProcessed)
                        globalCount += usernameLinks.Count;
                }

                if (!pageProcessed)
                    segmentProgress?.Invoke(segmentIndex, $"[{rangeLabel}] Skipping page {pageNum} after {maxRetries} retries.");
            }
        }
        catch (OperationCanceledException)
        {
            segmentProgress?.Invoke(segmentIndex, $"Cancelled");
        }
        catch (Exception ex)
        {
            segmentProgress?.Invoke(segmentIndex, $"Error: {ex.Message}");
            Debug.WriteLine($"[Chaturbate] Exception in segment: {ex}");
        }

        return (output, startPage, endPage);
    }

    // =========================================================================
    // Helper methods
    // =========================================================================
    public async Task EnsureConsentAsync(IPage page, CancellationToken cancellationToken = default)
    {
        if (_consentHandled.Contains(Site.Chaturbate)) return;

        try
        {
            var agreeButton = await page.WaitForSelectorAsync("a#close_entrance_terms", new PageWaitForSelectorOptions { Timeout = 10000 });
            if (agreeButton != null)
            {
                await agreeButton.ClickAsync();
                await Task.Delay(1000, cancellationToken);
                Debug.WriteLine("[Chaturbate] Consent accepted.");
            }
        }
        catch (TimeoutException) { }

        lock (_consentLock) _consentHandled.Add(Site.Chaturbate);
    }

    private void UiLog(string message, List<string> output, IProgress<string>? progress)
    {
        output.Add(message);
        progress?.Report(message);
    }
}