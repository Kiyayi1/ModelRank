using ModelRank.Models;

namespace ModelRank.Services;

public interface IMonitoringService
{
    SiteMonitorState GetState(Site site);
    Task StartMonitoringAsync(Site site, string modelName, double intervalMinutes);
    void StopMonitoring(Site site);
    event Action<Site>? StateChanged; // for UI updates
}