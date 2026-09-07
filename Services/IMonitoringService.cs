using ModelRank.Models;

namespace ModelRank.Services;

public interface IMonitoringService
{
    // All trackers currently registered for a site (running or stopped), one per distinct model name.
    IReadOnlyList<SiteMonitorState> GetStates(Site site);
    Task StartMonitoringAsync(Site site, string modelName, double intervalMinutes);
    void StopMonitoring(Site site, string modelName);
    // Drops a stopped tracker from the list entirely (no-op while it is still monitoring).
    void RemoveTracker(Site site, string modelName);
    event Action<Site, string>? StateChanged; // site + normalized model key, for UI updates
}
