using Microsoft.Extensions.Logging;
using ModelRank.Services;

namespace ModelRank
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            string appData = AppContext.BaseDirectory;
            string browserPath = Path.Combine(appData, "ModelRank", "PlaywrightBrowsers");
            Directory.CreateDirectory(browserPath);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserPath);


            var builder = MauiApp.CreateBuilder();

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            // Register the scraping service as scoped (or singleton)
            builder.Services.AddSingleton<IBrowserService, BrowserService>();
            builder.Services.AddSingleton<TorProxyManager>();
            builder.Services.AddSingleton<ChaturbateScraper>();
            builder.Services.AddSingleton<CamsodaScraper>();
            builder.Services.AddSingleton<Cam4Scraper>();
            builder.Services.AddSingleton<ISiteScraperFactory, SiteScraperFactory>();
            // Register concrete singleton first
            builder.Services.AddSingleton<MonitoringService>();
            // Then register the interface to return the same instance
            builder.Services.AddSingleton<IMonitoringService>(sp => sp.GetRequiredService<MonitoringService>());
            builder.Services.AddSingleton<IStorageService, JsonStorageService>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif
            var app = builder.Build();
            var browserService = app.Services.GetRequiredService<IBrowserService>();
            // Optionally, dispose on application exit (platform-specific)

            return app;
            //return builder.Build();
        }
    }
}
