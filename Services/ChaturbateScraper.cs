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

    public async Task<List<string>> FindModelRankAsync(IPage page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        UiLog($"Starting new search for '{modelName}' from page 1.", output, progress);
        Debug.WriteLine($"[Chaturbate] Starting search for {modelName}");

        page.SetDefaultTimeout(600_000); // 10 minutes (increased)

        int pageNum = 1;
        bool found = false;
        int globalCount = 0;
        const int maxRetries = 10; // much higher

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

                    // Random delay before navigation (3-7 seconds)
                    await Task.Delay(_random.Next(3000, 7000), cancellationToken);
                    Debug.WriteLine($"[Chaturbate] Navigating to {url}");
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    Debug.WriteLine("[Chaturbate] DOM ready");

                    var title = await page.TitleAsync();
                    Debug.WriteLine($"[Chaturbate] Page title: {title}");

                    // Check page content for Cloudflare challenge (more reliable)
                    var content = await page.ContentAsync();
                    bool isChallenge = title.Contains("Just a moment") ||
                                      title.Contains("security verification") ||
                                      title.Contains("Cloudflare") ||
                                      content.Contains("Ray ID:") ||
                                      content.Contains("Performing security verification");

                    if (isChallenge)
                    {
                        retryCount++;
                        // Exponential backoff starting at 30 seconds, up to 300 seconds (5 minutes)
                        int waitSeconds = (int)Math.Pow(2, retryCount) * 15;
                        if (waitSeconds > 300) waitSeconds = 300;
                        Debug.WriteLine($"[Chaturbate] Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s (retry {retryCount}/{maxRetries})...");
                        UiLog($"Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s...", output, progress);

                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string screenshotsDir = Path.Combine(AppContext.BaseDirectory, "DebugScreenshots");
                        Directory.CreateDirectory(screenshotsDir);
                        var screenshotPath = Path.Combine(screenshotsDir, $"challenge_page_{pageNum}_{timestamp}.png");
                        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
                        Debug.WriteLine($"[Chaturbate] Challenge screenshot saved to {screenshotPath}");

                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(waitSeconds * 1000, cancellationToken);
                            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                            continue;
                        }
                        else
                        {
                            UiLog($"Cloudflare challenge persisted after {maxRetries} retries. Moving to next page.", output, progress);
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
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string screenshotsDir = Path.Combine(AppContext.BaseDirectory, "DebugScreenshots");
                        Directory.CreateDirectory(screenshotsDir);
                        var screenshotPath = Path.Combine(screenshotsDir, $"cards_timeout_page_{pageNum}_{timestamp}.png");
                        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
                        UiLog($"Timeout waiting for cards on page {pageNum} (retry {retryCount}/{maxRetries}). Screenshot saved to {screenshotPath}", output, progress);
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
        catch (Exception ex) when (ex.GetType().Name == "TargetClosedException")
        {
            UiLog("Search cancelled (browser closed).", output, progress);
            Debug.WriteLine("[Chaturbate] Search cancelled (TargetClosedException)");
        }
        catch (Exception ex)
        {
            UiLog($"Error: {ex.Message}", output, progress);
            Debug.WriteLine($"[Chaturbate] Exception: {ex}");
        }

        return output;
    }

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