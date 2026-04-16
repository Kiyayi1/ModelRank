using Microsoft.Playwright;
using ModelRank.Models;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace ModelRank.Services;

public class BrowserService : IBrowserService
{
    private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
    private IPlaywright? _playwright;
    private Microsoft.Playwright.IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly ConcurrentDictionary<Site, IPage> _pages = new();
    private bool _disposed;
    private const bool Headless = true;

    private static readonly string UserDataDir = Path.Combine(AppContext.BaseDirectory, "PlaywrightUserData");

    static BrowserService()
    {
        Directory.CreateDirectory(UserDataDir);
    }

    public async Task<IPage> GetOrCreatePageAsync(Site site, IProgress<string>? progress = null)
    {
        // If we already have a page for this site and it's still usable, return it
        if (_pages.TryGetValue(site, out var existingPage) && existingPage != null && !existingPage.IsClosed)
            return existingPage;

        await _initLock.WaitAsync();
        try
        {
            // Double-check after lock
            if (_pages.TryGetValue(site, out existingPage) && existingPage != null && !existingPage.IsClosed)
                return existingPage;

            // Ensure browsers are installed (first run only)
            await PlaywrightInstaller.EnsureBrowsersInstalledAsync(progress);

            if (_playwright == null)
            {
                _playwright = await Playwright.CreateAsync();

                // Launch Firefox (headless mode controlled by const)
                _browser = await _playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = Headless,  // true for production, false for debugging
                    Args = new[] { "--no-sandbox" }
                });

                _context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/119.0",
                    Locale = "en-US",
                    TimezoneId = "Africa/Nairobi",
                    ScreenSize = new ScreenSize { Width = 1920, Height = 1080 }
                });

                // Stealth script for Firefox (overrides navigator.webdriver, etc.)
                await _context.AddInitScriptAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            }");
            }

            var page = await _context!.NewPageAsync();
            _pages[site] = page;
            return page;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            foreach (var page in _pages.Values)
                try { await page.CloseAsync(); } catch { }
            _pages.Clear();

            if (_context != null)
                try { await _context.CloseAsync(); } catch { }
            _context = null;

            if (_browser != null)
                try { await _browser.CloseAsync(); } catch { }
            _browser = null;

            if (_playwright != null)
                _playwright.Dispose();
            _playwright = null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task ClosePageAsync(Site site)
    {
        if (_pages.TryRemove(site, out var page))
            try { await page.CloseAsync(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ResetAsync();
        _initLock.Dispose();
        _disposed = true;
    }
}