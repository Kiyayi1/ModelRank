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

    public async Task<List<string>> FindModelRankAsync(IPage page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        UiLog($"Starting new search for '{modelName}' from page 1.", output, progress);

        int pageNum = 1;
        bool found = false;
        int globalCount = 0;
        int? lastPage = null;

        try
        {
            while (!found && !cancellationToken.IsCancellationRequested)
            {
                string url = pageNum == 1 ? "https://www.camsoda.com/" : $"https://www.camsoda.com/?p={pageNum}";
                UiLog($"Scanning page {pageNum}...", output, progress);

                cancellationToken.ThrowIfCancellationRequested();

                bool navigationSuccess = await NavigateWithRetryAsync(page, url, output, progress, cancellationToken);
                if (!navigationSuccess)
                {
                    UiLog($"Failed to load page {pageNum} after retries. Aborting search.", output, progress);
                    break;
                }

                if (pageNum == 1 && !_consentHandled.Contains(Site.Camsoda))
                {
                    await HandleConsentAsync(page, output, progress, cancellationToken);
                    lock (_consentLock) _consentHandled.Add(Site.Camsoda);
                }

                // Re‑check last page on each page until we have a valid number
                int? newLastPage = await GetLastPageNumberAsync(page);
                if (newLastPage.HasValue && newLastPage.Value > 1)
                {
                    if (!lastPage.HasValue || newLastPage.Value > lastPage.Value)
                    {
                        lastPage = newLastPage.Value;
                        Debug.WriteLine($"[Camsoda] Last page is {lastPage.Value}");
                    }
                }

                // Check for redirect (non‑existent page redirects to page 1)
                int currentPage = ExtractPageNumber(page.Url);
                if (currentPage != pageNum && pageNum > 1)
                {
                    UiLog($"Requested page {pageNum} does not exist – redirected to page {currentPage}. End of listings.", output, progress);
                    break;
                }

                // If we know the last page and we've passed it, stop
                if (lastPage.HasValue && pageNum > lastPage.Value)
                {
                    UiLog($"Reached last page ({lastPage.Value}) without finding the model.", output, progress);
                    break;
                }

                var (cards, shouldStop) = await ProcessPageAsync(page, pageNum, output, progress, cancellationToken);
                if (shouldStop)
                {
                    UiLog($"End of listings reached at page {pageNum}.", output, progress);
                    break;
                }

                if (cards != null && cards.Count > 0)
                {
                    found = await SearchCardsAsync(cards, modelName, pageNum, globalCount, output, progress, cancellationToken);
                    if (!found)
                    {
                        globalCount += cards.Count;
                    }
                }
                else
                {
                    Debug.WriteLine($"[Camsoda] Page {pageNum} has no cards, moving to next page.");
                }

                if (!found)
                {
                    // Double‑check we haven't exceeded the last page before moving on
                    if (lastPage.HasValue && pageNum >= lastPage.Value)
                    {
                        UiLog($"Reached last page ({lastPage.Value}) without finding the model.", output, progress);
                        break;
                    }
                    pageNum++;
                    int delay = _random.Next(2000, 4000);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            UiLog("Search cancelled.", output, progress);
        }
        catch (Exception ex)
        {
            UiLog($"Error: {ex.Message}", output, progress);
            Debug.WriteLine($"[Camsoda] Exception: {ex}");
        }

        return output;
    }

    private async Task<bool> NavigateWithRetryAsync(IPage page, string url, List<string> output, IProgress<string>? progress, CancellationToken token, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 15000
                });
                return true;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"[Camsoda] Navigation timeout (attempt {attempt})");
                if (attempt == maxRetries) return false;
                await Task.Delay(2000 * attempt, token);
            }
        }
        return false;
    }

    private async Task HandleConsentAsync(IPage page, List<string> output, IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            var consentButton = await page.WaitForSelectorAsync("button:has-text('I am over 18 - ENTER SITE')", new PageWaitForSelectorOptions { Timeout = 8000 });
            if (consentButton != null)
            {
                await consentButton.ClickAsync();
                await Task.Delay(1000, token);
                Debug.WriteLine("[Camsoda] Consent accepted.");
            }
        }
        catch (TimeoutException) { }
    }

    private async Task<(IReadOnlyList<IElementHandle>? cards, bool shouldStop)> ProcessPageAsync(IPage page, int pageNum, List<string> output, IProgress<string>? progress, CancellationToken token)
    {
        const int maxRetrySeconds = 120; // 2 minutes total per page
        var startTime = DateTime.UtcNow;

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxRetrySeconds && !token.IsCancellationRequested)
        {
            bool hasCardsIndicator = false;
            bool hasPagination = false;
            try
            {
                var waitResult = await page.WaitForFunctionAsync(@"() => {
                    return {
                        cards: document.querySelector('a.index-module__wrapper--XqyW8') !== null,
                        pagination: document.querySelector('button.index-module__active--axi6h') !== null
                    };
                }", new PageWaitForFunctionOptions { Timeout = 15000 });
                var result = await waitResult.JsonValueAsync<dynamic>();
                hasCardsIndicator = result.cards;
                hasPagination = result.pagination;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"[Camsoda] Page {pageNum} – no cards or pagination after initial wait.");
            }

            // Attempt to fetch cards with incremental delays (2,4,6,10 seconds)
            int[] delays = { 2000, 4000, 6000, 10000 };
            foreach (var delayMs in delays)
            {
                token.ThrowIfCancellationRequested();
                var cards = await page.QuerySelectorAllAsync("a.index-module__wrapper--XqyW8");
                if (cards.Count > 0)
                {
                    Debug.WriteLine($"[Camsoda] Page {pageNum}: found {cards.Count} cards after attempt.");
                    return (cards, false);
                }
                Debug.WriteLine($"[Camsoda] Page {pageNum}: no cards after {delayMs / 1000}s, waiting...");
                await Task.Delay(delayMs, token);
            }

            // After incremental attempts, still no cards. Check if time remains.
            if ((DateTime.UtcNow - startTime).TotalSeconds < maxRetrySeconds - 5)
            {
                Debug.WriteLine($"[Camsoda] Page {pageNum} – no cards after attempts. Refreshing...");
                await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Task.Delay(2000, token);
                continue;
            }
            else
            {
                // Out of time, check next button to decide end.
                var nextButton = await page.QuerySelectorAsync("button.index-module__next--nG4HM:not([disabled])");
                if (nextButton != null)
                {
                    Debug.WriteLine($"[Camsoda] Page {pageNum} – no cards after {maxRetrySeconds}s, but next button exists. Moving to next page.");
                    return (null, false);
                }
                else
                {
                    Debug.WriteLine($"[Camsoda] Page {pageNum} – no cards and no next button after {maxRetrySeconds}s. End of listings.");
                    return (null, true);
                }
            }
        }

        // Time's up – check next button one last time
        var finalNext = await page.QuerySelectorAsync("button.index-module__next--nG4HM:not([disabled])");
        if (finalNext != null)
        {
            Debug.WriteLine($"[Camsoda] Page {pageNum} – timed out after {maxRetrySeconds}s, but next button exists. Moving to next page.");
            return (null, false);
        }
        else
        {
            Debug.WriteLine($"[Camsoda] Page {pageNum} – timed out after {maxRetrySeconds}s with no next button. End of listings.");
            return (null, true);
        }
    }

    private async Task<bool> SearchCardsAsync(IReadOnlyList<IElementHandle> cards, string modelName, int pageNum, int globalCount, List<string> output, IProgress<string>? progress, CancellationToken token)
    {
        UiLog($"Page {pageNum}: found {cards.Count} models.", output, progress);

        var sampleTasks = cards.Take(5).Select(async card =>
        {
            var username = await card.GetAttributeAsync("data-username");
            return username ?? "null";
        });
        var samples = await Task.WhenAll(sampleTasks);
        Debug.WriteLine($"[Camsoda] Sample usernames: {string.Join(", ", samples)}");

        for (int i = 0; i < cards.Count; i++)
        {
            token.ThrowIfCancellationRequested();
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
                {
                    displayName = fullText.Trim();
                }
            }

            var viewersSpanForCount = await card.QuerySelectorAsync("span.index-module__infoConnectionCount--rl4gs");
            string viewers = "N/A";
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
                return true;
            }
        }
        return false;
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

    private async Task<int?> GetLastPageNumberAsync(IPage page)
    {
        try
        {
            // First, find all buttons with aria-label containing a number (page buttons)
            var pageButtons = await page.QuerySelectorAllAsync("button[aria-label]");
            int maxPage = 0;
            foreach (var btn in pageButtons)
            {
                var ariaLabel = await btn.GetAttributeAsync("aria-label");
                if (int.TryParse(ariaLabel, out int pageNum))
                {
                    if (pageNum > maxPage)
                        maxPage = pageNum;
                }
            }
            if (maxPage > 0) return maxPage;

            // Fallback: look for any button with numeric text (excluding prev/next)
            var allButtons = await page.QuerySelectorAllAsync("button");
            foreach (var btn in allButtons)
            {
                var text = await btn.TextContentAsync();
                if (int.TryParse(text?.Trim(), out int pageNum))
                {
                    if (pageNum > maxPage)
                        maxPage = pageNum;
                }
            }
            if (maxPage > 0) return maxPage;
        }
        catch { }
        return null;
    }
}