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

public class Cam4Scraper : ISiteScraper
{
    private static readonly Random _random = new Random();
    private static readonly HashSet<Site> _consentHandled = new HashSet<Site>();
    private static readonly object _consentLock = new object();

    private static readonly string[] _userAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/119.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:108.0) Gecko/20100101 Firefox/108.0"
    };

    public async Task<List<string>> FindModelRankAsync(IPage page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        UiLog($"Starting new search for '{modelName}' from page 1.", output, progress);
        Debug.WriteLine($"[Cam4] Starting search for {modelName}");

        page.SetDefaultTimeout(300_000);

        int pageNum = 1;
        bool found = false;
        int globalCount = 0;
        const int maxRetries = 3;

        try
        {
            while (!found && !cancellationToken.IsCancellationRequested)
            {
                string url = pageNum == 1 ? "https://www.cam4.com/all" : $"https://www.cam4.com/all/?page={pageNum}";
                UiLog($"Scanning page {pageNum}...", output, progress);
                Debug.WriteLine($"[Cam4] Scanning page {pageNum}");

                bool pageProcessed = false;
                int retryCount = 0;

                while (!pageProcessed && !cancellationToken.IsCancellationRequested && retryCount < maxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var userAgent = _userAgents[_random.Next(_userAgents.Length)];
                    await page.Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { { "User-Agent", userAgent } });
                    Debug.WriteLine($"[Cam4] Using user agent: {userAgent}");

                    await Task.Delay(_random.Next(2000, 5000), cancellationToken);
                    Debug.WriteLine($"[Cam4] Navigating to {url}");
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    Debug.WriteLine("[Cam4] DOM ready");

                    var title = await page.TitleAsync();
                    Debug.WriteLine($"[Cam4] Page title: {title}");

                    // End of listings detection
                    var noResults = await page.QuerySelectorAsync("div.RmjgJ");
                    if (noResults != null)
                    {
                        UiLog($"No results found on page {pageNum}. End of listings.", output, progress);
                        output.Add("LAST_PAGE_REACHED");
                        return output;
                    }

                    // Cloudflare / browser update detection
                    var content = await page.ContentAsync();
                    if (title.Contains("Just a moment") || title.Contains("security verification") || title.Contains("Cloudflare") || content.Contains("Browser Update") || content.Contains("Ray ID:"))
                    {
                        retryCount++;
                        int waitSeconds = (int)Math.Pow(2, retryCount);
                        Debug.WriteLine($"[Cam4] Challenge on page {pageNum}, waiting {waitSeconds}s (retry {retryCount}/{maxRetries})...");
                        UiLog($"Challenge on page {pageNum}, waiting {waitSeconds}s...", output, progress);
                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(waitSeconds * 1000, cancellationToken);
                            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                            continue;
                        }
                        else
                        {
                            UiLog($"Challenge persisted after {maxRetries} retries. Moving to next page.", output, progress);
                            pageProcessed = true;
                            break;
                        }
                    }

                    // Consent on first page
                    if (pageNum == 1 && !_consentHandled.Contains(Site.Cam4))
                    {
                        Debug.WriteLine("[Cam4] Checking consent button");
                        bool consentClicked = false;

                        var agreeSelectors = new[]
                        {
                            "button:has-text('AGREE')",
                            "button:has-text('I AGREE')",
                            "button[data-id='Agree']",
                            "a:has-text('AGREE')"
                        };

                        foreach (var selector in agreeSelectors)
                        {
                            try
                            {
                                var agreeButton = await page.QuerySelectorAsync(selector);
                                if (agreeButton != null && await agreeButton.IsVisibleAsync())
                                {
                                    await agreeButton.ClickAsync();
                                    await Task.Delay(2000, cancellationToken);
                                    consentClicked = true;
                                    UiLog($"Consent accepted via {selector}.", output, progress);
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (!consentClicked)
                        {
                            try
                            {
                                var result = await page.EvaluateAsync<bool>("() => { const btns = document.querySelectorAll('button'); for(let btn of btns) { if(btn.innerText.includes('AGREE')) { btn.click(); return true; } } return false; }");
                                if (result)
                                {
                                    Debug.WriteLine("[Cam4] Consent clicked via JavaScript");
                                    await Task.Delay(2000, cancellationToken);
                                    consentClicked = true;
                                    UiLog("Consent accepted (JS fallback).", output, progress);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Cam4] JS consent click failed: {ex.Message}");
                            }
                        }

                        if (consentClicked)
                            await Task.Delay(3000, cancellationToken);

                        lock (_consentLock) _consentHandled.Add(Site.Cam4);
                    }

                    // Wait for cards
                    IReadOnlyList<IElementHandle>? cards = null;
                    try
                    {
                        await page.WaitForSelectorAsync("div.mZ7td[data-section-id='Broadcast Thumbnail']", new PageWaitForSelectorOptions { Timeout = 60000 });
                        cards = await page.QuerySelectorAllAsync("div.mZ7td[data-section-id='Broadcast Thumbnail']");
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
                        var nameEl = await card.QuerySelectorAsync("div.MvVrh");
                        return nameEl == null ? "null" : (await nameEl.TextContentAsync())?.Trim() ?? "empty";
                    });
                    var samples = await Task.WhenAll(sampleTasks);
                    output.Add($"Sample usernames: {string.Join(", ", samples)}");
                    Debug.WriteLine($"[Cam4] Sample usernames: {string.Join(", ", samples)}");

                    for (int i = 0; i < cards.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var card = cards[i];
                        var nameElement = await card.QuerySelectorAsync("div.MvVrh");
                        if (nameElement == null) continue;
                        var username = await nameElement.TextContentAsync();
                        username = username?.Trim() ?? "";

                        string viewers = "N/A";
                        var viewersElement = await card.QuerySelectorAsync("div.lAOFo");
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
                            Debug.WriteLine($"[Cam4] FOUND: {foundMsg}");
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
            Debug.WriteLine("[Cam4] Search cancelled");
        }
        catch (Exception ex) when (ex.GetType().Name == "TargetClosedException")
        {
            UiLog("Search cancelled (browser closed).", output, progress);
            Debug.WriteLine("[Cam4] Search cancelled (TargetClosedException)");
        }
        catch (Exception ex)
        {
            UiLog($"Error: {ex.Message}", output, progress);
            Debug.WriteLine($"[Cam4] Exception: {ex}");
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
        var match = Regex.Match(url, @"[?&]page=(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int page))
            return page;
        return 1;
    }
}