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

public class Cam4Scraper : ISiteScraper
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
                string url = pageNum == 1 ? "https://www.cam4.com/all" : $"https://www.cam4.com/all/?page={pageNum}";
                UiLog($"Scanning page {pageNum}...", output, progress);

                cancellationToken.ThrowIfCancellationRequested();

                bool navigationSuccess = await NavigateWithRetryAsync(page, url, output, progress, cancellationToken);
                if (!navigationSuccess)
                {
                    UiLog($"Failed to load page {pageNum} after retries. Aborting search.", output, progress);
                    break;
                }

                if (pageNum == 1 && !_consentHandled.Contains(Site.Cam4))
                {
                    await HandleConsentAsync(page, output, progress, cancellationToken);
                    lock (_consentLock) _consentHandled.Add(Site.Cam4);
                }

                // Get the last page number from pagination (if not already known)
                if (!lastPage.HasValue)
                {
                    lastPage = await GetLastPageNumberAsync(page);
                    if (lastPage.HasValue && lastPage.Value > 1)
                        Debug.WriteLine($"[Cam4] Last page is {lastPage.Value}");
                }

                // Check for redirect (non‑existent page redirects to page 1)
                int currentPage = ExtractPageNumber(page.Url);
                if (currentPage != pageNum && pageNum > 1)
                {
                    UiLog($"Requested page {pageNum} does not exist – redirected to page {currentPage}. End of listings.", output, progress);
                    break;
                }

                // If we've passed the last page, stop
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
                    Debug.WriteLine($"[Cam4] Page {pageNum} has no cards, moving to next page.");
                }

                if (!found)
                {
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
            Debug.WriteLine($"[Cam4] Exception: {ex}");
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
                Debug.WriteLine($"[Cam4] Navigation timeout (attempt {attempt})");
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
            var dialog = await page.WaitForSelectorAsync("dialog[data-id='AgeConsentDisclaimer']", new PageWaitForSelectorOptions { Timeout = 15000 });
            if (dialog != null)
            {
                var agreeButton = await dialog.QuerySelectorAsync("button[data-id='Agree']");
                if (agreeButton != null)
                {
                    await agreeButton.ClickAsync();
                    await Task.Delay(2000, token);
                    Debug.WriteLine("[Cam4] Consent accepted.");
                }
            }
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("[Cam4] No consent dialog found – proceeding.");
        }
    }

    private async Task<(IReadOnlyList<IElementHandle>? cards, bool shouldStop)> ProcessPageAsync(IPage page, int pageNum, List<string> output, IProgress<string>? progress, CancellationToken token)
    {
        const int maxRetrySeconds = 120;
        var startTime = DateTime.UtcNow;

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxRetrySeconds && !token.IsCancellationRequested)
        {
            bool hasCardsIndicator = false;
            bool hasPagination = false;
            try
            {
                var waitResult = await page.WaitForFunctionAsync(@"() => {
                    return {
                        cards: document.querySelector('div.mZ7td[data-section-id=\'Broadcast Thumbnail\']') !== null,
                        pagination: document.querySelector('a[data-action-id=\'Last Page\']') !== null
                    };
                }", new PageWaitForFunctionOptions { Timeout = 15000 });
                var result = await waitResult.JsonValueAsync<dynamic>();
                hasCardsIndicator = result.cards;
                hasPagination = result.pagination;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"[Cam4] Page {pageNum} – no cards or pagination after initial wait.");
            }

            int[] delays = { 2000, 4000, 6000, 10000 };
            foreach (var delayMs in delays)
            {
                token.ThrowIfCancellationRequested();
                var cards = await page.QuerySelectorAllAsync("div.mZ7td[data-section-id='Broadcast Thumbnail']");
                if (cards.Count > 0)
                {
                    Debug.WriteLine($"[Cam4] Page {pageNum}: found {cards.Count} cards after attempt.");
                    return (cards, false);
                }
                Debug.WriteLine($"[Cam4] Page {pageNum}: no cards after {delayMs / 1000}s, waiting...");
                await Task.Delay(delayMs, token);
            }

            if ((DateTime.UtcNow - startTime).TotalSeconds < maxRetrySeconds - 5)
            {
                Debug.WriteLine($"[Cam4] Page {pageNum} – no cards after attempts. Refreshing...");
                await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Task.Delay(2000, token);
                continue;
            }
            else
            {
                var nextButton = await page.QuerySelectorAsync("a[data-action-id='Next Page']:not([disabled])");
                if (nextButton != null)
                {
                    Debug.WriteLine($"[Cam4] Page {pageNum} – no cards after {maxRetrySeconds}s, but next button exists. Moving to next page.");
                    return (null, false);
                }
                else
                {
                    Debug.WriteLine($"[Cam4] Page {pageNum} – no cards and no next button after {maxRetrySeconds}s. End of listings.");
                    return (null, true);
                }
            }
        }

        var finalNext = await page.QuerySelectorAsync("a[data-action-id='Next Page']:not([disabled])");
        if (finalNext != null)
        {
            Debug.WriteLine($"[Cam4] Page {pageNum} – timed out after {maxRetrySeconds}s, but next button exists. Moving to next page.");
            return (null, false);
        }
        else
        {
            Debug.WriteLine($"[Cam4] Page {pageNum} – timed out after {maxRetrySeconds}s with no next button. End of listings.");
            return (null, true);
        }
    }

    private async Task<bool> SearchCardsAsync(IReadOnlyList<IElementHandle> cards, string modelName, int pageNum, int globalCount, List<string> output, IProgress<string>? progress, CancellationToken token)
    {
        UiLog($"Page {pageNum}: found {cards.Count} models.", output, progress);

        var sampleTasks = cards.Take(5).Select(async card =>
        {
            var nameEl = await card.QuerySelectorAsync("div.MvVrh");
            return nameEl == null ? "null" : (await nameEl.TextContentAsync())?.Trim() ?? "empty";
        });
        var samples = await Task.WhenAll(sampleTasks);
        Debug.WriteLine($"[Cam4] Sample usernames: {string.Join(", ", samples)}");

        for (int i = 0; i < cards.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var card = cards[i];

            var nameElement = await card.QuerySelectorAsync("div.MvVrh");
            if (nameElement == null) continue;
            var username = await nameElement.TextContentAsync();
            username = username?.Trim() ?? "";

            var viewersElement = await card.QuerySelectorAsync("div.lAOFo");
            string viewers = "N/A";
            if (viewersElement != null)
            {
                var viewersText = await viewersElement.TextContentAsync();
                viewers = viewersText?.Trim() ?? "N/A";
                var match = Regex.Match(viewersText, @"(\d+)");
                if (match.Success)
                    viewers = match.Value;
            }

            if (username.Equals(modelName, StringComparison.OrdinalIgnoreCase))
            {
                int localPos = i + 1;
                int totalRank = globalCount + localPos;
                var foundMsg = $"Found '{modelName}' (display: {username}) on page {pageNum}, position {localPos} (overall rank: {totalRank}) | Viewers: {viewers}";
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
        var match = Regex.Match(url, @"[?&]page=(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int page))
            return page;
        return 1;
    }

    private async Task<int?> GetLastPageNumberAsync(IPage page)
    {
        try
        {
            var lastPageLink = await page.QuerySelectorAsync("a[data-action-id='Last Page']");
            if (lastPageLink != null)
            {
                var lastPageAttr = await lastPageLink.GetAttributeAsync("data-value");
                if (int.TryParse(lastPageAttr, out int last))
                    return last;
            }
        }
        catch { }
        return null;
    }
}