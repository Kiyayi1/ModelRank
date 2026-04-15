namespace ModelRank.Models;

public class SiteMonitorState
{
    public string ModelName { get; set; } = "";
    public double IntervalMinutes { get; set; } = 5;
    public bool IsMonitoring { get; set; }
    public bool IsSearching { get; set; }
    public string StatusMessage { get; set; } = "";
    public List<SearchResult> Results { get; set; } = new();
    public CancellationTokenSource? CancellationTokenSource { get; set; }
    public DateTime NextSearchTime { get; set; }
}