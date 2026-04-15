using Knapcode.TorSharp;
using System.Net;

namespace ModelRank.Services;

public class TorProxyManager : IAsyncDisposable
{
    private TorSharpProxy? _torSharpProxy;
    private readonly TorSharpSettings _settings;
    private bool _started;

    public TorProxyManager()
    {
        _settings = new TorSharpSettings
        {
            ZippedToolsDirectory = Path.Combine(Path.GetTempPath(), "TorSharpZipped"),
            ExtractedToolsDirectory = Path.Combine(Path.GetTempPath(), "TorSharpExtracted"),
            PrivoxySettings = { Disable = true },
            TorSettings =
            {
                SocksPort = 9050,
                ControlPort = 9051,
                ControlPassword = "torpassword"
            }
        };
    }

    public async Task StartAsync()
    {
        if (_started) return;

        using var httpClient = new HttpClient();
        var fetcher = new TorSharpToolFetcher(_settings, httpClient);
        await fetcher.FetchAsync();

        _torSharpProxy = new TorSharpProxy(_settings);
        await _torSharpProxy.ConfigureAndStartAsync();
        _started = true;
    }

    public string GetProxyUrl(int instanceId = 0)
    {
        int port = _settings.TorSettings.SocksPort + instanceId;
        return $"socks5://localhost:{port}";
    }

    public void Stop()
    {
        if (_torSharpProxy != null)
        {
            _torSharpProxy.Stop();
            _torSharpProxy.Dispose();
            _torSharpProxy = null;
        }
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }
}