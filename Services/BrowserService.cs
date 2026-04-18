using Microsoft.Playwright;
using ModelRank.Models;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using IBrowser = Microsoft.Playwright.IBrowser;

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

    private IBrowser? _browserFirefox;
    private IBrowser? _browserChromium;
    private IBrowserContext? _contextFirefox;
    private IBrowserContext? _contextChromium;

    private static readonly string UserDataDir = Path.Combine(AppContext.BaseDirectory, "PlaywrightUserData");

    static BrowserService()
    {
        Directory.CreateDirectory(UserDataDir);
    }

    public async Task<IPage> GetOrCreatePageAsync(Site site, IProgress<string>? progress = null)
    {
        // If we already have a page for this site, return it
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

            // Choose browser type based on site
            if (site == Site.Chaturbate)
            {
                // Use Firefox for Chaturbate (better stealth)
                if (_playwright == null)
                    _playwright = await Playwright.CreateAsync();

                if (_browserFirefox == null)
                {
                    _browserFirefox = await _playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = Headless,
                        Args = new[] { "--no-sandbox" }
                    });
                    _contextFirefox = await _browserFirefox.NewContextAsync(new BrowserNewContextOptions
                    {
                        ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/119.0",
                        Locale = "en-US",
                        TimezoneId = "Africa/Nairobi",
                        ScreenSize = new ScreenSize { Width = 1920, Height = 1080 }
                    });
                    await _contextFirefox.AddInitScriptAsync(@"() => {
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                }");
                }

                var page = await _contextFirefox!.NewPageAsync();
                _pages[site] = page;
                return page;
            }
            else
            {
                // Use Chromium for Camsoda and Cam4
                if (_playwright == null)
                    _playwright = await Playwright.CreateAsync();

                if (_browserChromium == null)
                {
                    _browserChromium = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = Headless,
                        Args = new[]
                        {
                        "--disable-blink-features=AutomationControlled",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-web-security",
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--disable-background-networking",
                        "--disable-default-apps",
                        "--disable-extensions",
                        "--disable-sync",
                        "--disable-translate",
                        "--hide-scrollbars",
                        "--metrics-recording-only",
                        "--mute-audio",
                        "--no-first-run",
                        "--password-store=basic"
                    }
                    });
                    _contextChromium = await _browserChromium.NewContextAsync(new BrowserNewContextOptions
                    {
                        ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                        Locale = "en-US",
                        TimezoneId = "Africa/Nairobi",
                        ScreenSize = new ScreenSize { Width = 1920, Height = 1080 }
                    });
                    await _contextChromium.AddInitScriptAsync(@"() => {
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                    const originalQuery = window.navigator.permissions.query;
                    window.navigator.permissions.query = (parameters) => (
                        parameters.name === 'notifications' ?
                            Promise.resolve({ state: Notification.permission }) :
                            originalQuery(parameters)
                    );
                }");
                }

                var page = await _contextChromium!.NewPageAsync();
                _pages[site] = page;
                return page;
            }
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

            if (_contextFirefox != null)
                try { await _contextFirefox.CloseAsync(); } catch { }
            _contextFirefox = null;

            if (_contextChromium != null)
                try { await _contextChromium.CloseAsync(); } catch { }
            _contextChromium = null;

            if (_browserFirefox != null)
                try { await _browserFirefox.CloseAsync(); } catch { }
            _browserFirefox = null;

            if (_browserChromium != null)
                try { await _browserChromium.CloseAsync(); } catch { }
            _browserChromium = null;

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