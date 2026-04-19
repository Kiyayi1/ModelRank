using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelRank.Models;

namespace ModelRank.Services;

public class CamsodaScraper : ISiteScraper
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
        Debug.WriteLine($"[Camsoda] Starting search for {modelName}");

        page.SetDefaultTimeout(300_000);

        int pageNum = 1;
        bool found = false;
        int globalCount = 0;
        const int maxRetries = 3;

        try
        {
            while (!found && !cancellationToken.IsCancellationRequested)
            {
                string url = pageNum == 1 ? "https://www.camsoda.com/" : $"https://www.camsoda.com/?p={pageNum}";
                UiLog($"Scanning page {pageNum}...", output, progress);
                Debug.WriteLine($"[Camsoda] Scanning page {pageNum}");

                bool pageProcessed = false;
                int retryCount = 0;

                while (!pageProcessed && !cancellationToken.IsCancellationRequested && retryCount < maxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var userAgent = _userAgents[_random.Next(_userAgents.Length)];
                    await page.Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { { "User-Agent", userAgent } });
                    Debug.WriteLine($"[Camsoda] Using user agent: {userAgent}");

                    await Task.Delay(_random.Next(2000, 5000), cancellationToken);
                    Debug.WriteLine($"[Camsoda] Navigating to {url}");
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    Debug.WriteLine("[Camsoda] DOM ready");

                    // End of listings detection
                    var noResults = await page.QuerySelectorAsync("div[data-camsoda-response-code='404']");
                    if (noResults != null)
                    {
                        UiLog($"No more cams found on page {pageNum}. End of listings.", output, progress);
                        output.Add("LAST_PAGE_REACHED");
                        return output;
                    }

                    var title = await page.TitleAsync();
                    Debug.WriteLine($"[Camsoda] Page title: {title}");

                    // Cloudflare challenge detection
                    var content = await page.ContentAsync();
                    if (title.Contains("Just a moment") || title.Contains("security verification") || title.Contains("Cloudflare") || content.Contains("Ray ID:"))
                    {
                        retryCount++;
                        int waitSeconds = (int)Math.Pow(2, retryCount);
                        Debug.WriteLine($"[Camsoda] Cloudflare challenge on page {pageNum}, waiting {waitSeconds}s (retry {retryCount}/{maxRetries})...");
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
                    if (pageNum == 1 && !_consentHandled.Contains(Site.Camsoda))
                    {
                        Debug.WriteLine("[Camsoda] Checking consent button");
                        bool consentClicked = false;

                        try
                        {
                            var consentButton = await page.WaitForSelectorAsync("button:has-text('I am over 18 - ENTER SITE')", new PageWaitForSelectorOptions { Timeout = 8000 });
                            if (consentButton != null)
                            {
                                await consentButton.ClickAsync();
                                await Task.Delay(1000, cancellationToken);
                                consentClicked = true;
                                UiLog("Consent accepted (normal click).", output, progress);
                            }
                        }
                        catch (TimeoutException)
                        {
                            Debug.WriteLine("[Camsoda] Consent button not found, trying JS fallback");
                        }

                        if (!consentClicked)
                        {
                            try
                            {
                                var result = await page.EvaluateAsync<bool>(@"() => {
                                    const btns = document.querySelectorAll('button');
                                    for(let btn of btns) {
                                        if(btn.innerText.includes('I am over 18 - ENTER SITE')) {
                                            btn.click();
                                            return true;
                                        }
                                    }
                                    return false;
                                }");
                                if (result)
                                {
                                    Debug.WriteLine("[Camsoda] Consent clicked via JavaScript");
                                    await Task.Delay(1000, cancellationToken);
                                    consentClicked = true;
                                    UiLog("Consent accepted (JS fallback).", output, progress);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Camsoda] JS consent click failed: {ex.Message}");
                            }
                        }

                        lock (_consentLock) _consentHandled.Add(Site.Camsoda);
                    }

                    // Wait for cards
                    IReadOnlyList<IElementHandle>? cards = null;
                    try
                    {
                        await page.WaitForSelectorAsync("a.index-module__wrapper--XqyW8", new PageWaitForSelectorOptions { Timeout = 60000 });
                        cards = await page.QuerySelectorAllAsync("a.index-module__wrapper--XqyW8");
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

                    if (cards == null || cards.Count == 0)
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
                    UiLog($"Page {pageNum}: found {cards.Count} models.", output, progress);

                    // Sample first 5 usernames
                    var sampleTasks = cards.Take(5).Select(async card =>
                    {
                        var username = await card.GetAttributeAsync("data-username");
                        return username ?? "null";
                    });
                    var samples = await Task.WhenAll(sampleTasks);
                    output.Add($"Sample usernames: {string.Join(", ", samples)}");
                    Debug.WriteLine($"[Camsoda] Sample usernames: {string.Join(", ", samples)}");

                    for (int i = 0; i < cards.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var card = cards[i];
                        var username = await card.GetAttributeAsync("data-username");
                        if (string.IsNullOrEmpty(username)) continue;

                        // Display name extraction
                        string displayName = username;
                        var displayNameElement = await card.QuerySelectorAsync("span.index-module__displayName--Y8mYz");
                        if (displayNameElement != null)
                        {
                            var fullText = await displayNameElement.TextContentAsync() ?? "";
                            var viewersSpan = await displayNameElement.QuerySelectorAsync("span.index-module__infoConnectionCount--rl4gs");
                            if (viewersSpan != null)
                            {
                                var viewersText = await viewersSpan.TextContentAsync() ?? "";
                                displayName = fullText.Replace(viewersText, "").Trim();
                            }
                            else
                                displayName = fullText.Trim();
                        }

                        string viewers = "N/A";
                        var viewersSpanForCount = await card.QuerySelectorAsync("span.index-module__infoConnectionCount--rl4gs");
                        if (viewersSpanForCount != null)
                        {
                            var viewersText = await viewersSpanForCount.TextContentAsync();
                            viewers = viewersText?.Trim() ?? "N/A";
                            var match = Regex.Match(viewersText, @"(\d+)");
                            if (match.Success)
                                viewers = match.Value;
                        }

                        if (username.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            int localPos = i + 1;
                            int totalRank = globalCount + localPos;
                            var foundMsg = $"Found '{modelName}' (display: {displayName}) on page {pageNum}, position {localPos} (overall rank: {totalRank}) | Viewers: {viewers}";
                            UiLog(foundMsg, output, progress);
                            Debug.WriteLine($"[Camsoda] FOUND: {foundMsg}");
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        globalCount += cards.Count;
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
            Debug.WriteLine("[Camsoda] Search cancelled");
        }
        catch (Exception ex) when (ex.GetType().Name == "TargetClosedException")
        {
            UiLog("Search cancelled (browser closed).", output, progress);
            Debug.WriteLine("[Camsoda] Search cancelled (TargetClosedException)");
        }
        catch (Exception ex)
        {
            UiLog($"Error: {ex.Message}", output, progress);
            Debug.WriteLine($"[Camsoda] Exception: {ex}");
        }

        return output;
    }

    private void UiLog(string message, List<string> output, IProgress<string>? progress)
    {
        output.Add(message);
        progress?.Report(message);
    }

    private int ExtractPageNumber(string url)
    {
        var match = Regex.Match(url, @"[?&]p=(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int page))
            return page;
        return 1;
    }
}