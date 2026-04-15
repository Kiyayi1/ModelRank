using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModelRank.Services;

public static class PlaywrightInstaller
{
    private static bool _installed = false;
    private static readonly object _lock = new object();

    public static async Task EnsureBrowsersInstalledAsync(IProgress<string>? progress = null)
    {
        if (_installed) return;

        lock (_lock)
        {
            if (_installed) return;
        }

        string browserPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrEmpty(browserPath) && Directory.Exists(Path.Combine(browserPath, "chromium")))
        {
            lock (_lock) { _installed = true; }
            progress?.Report("Playwright browsers found.");
            return;
        }

        progress?.Report("Playwright browsers not found. Downloading... (this may take a few minutes)");

        var spinner = new[] { '|', '/', '-', '\\' };
        int spinIndex = 0;
        var spinnerCts = new CancellationTokenSource();
        var spinnerTask = Task.Run(async () =>
        {
            while (!_installed && !spinnerCts.Token.IsCancellationRequested)
            {
                progress?.Report($"Downloading Playwright browsers... {spinner[spinIndex % spinner.Length]}");
                spinIndex++;
                await Task.Delay(500);
            }
        });

        try
        {
            await Task.Run(() =>
            {
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install" });
                if (exitCode != 0)
                    throw new Exception($"Installation failed with exit code {exitCode}");
            });
        }
        finally
        {
            _installed = true;
            spinnerCts.Cancel();
            await spinnerTask;
        }

        progress?.Report("Playwright browsers installed successfully.");
    }
}