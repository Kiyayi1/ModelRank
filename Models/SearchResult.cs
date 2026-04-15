namespace ModelRank.Models;

public class SearchResult
{
    public int Id { get; set; }
    public Site Site { get; set; }
    public string ModelName { get; set; } = "";          // the search term (username)
    public string DisplayName { get; set; } = "";        // the human‑readable name from the site
    public DateTime Timestamp { get; set; }
    public int Page { get; set; }
    public int Position { get; set; }
    public int Rank { get; set; }
    public string Viewers { get; set; } = "N/A";
    public bool Found { get; set; }

    // UI-only trend indicators
    public int? PageChange { get; set; }
    public int? PositionChange { get; set; }
    public int? RankChange { get; set; }
    public int? ViewersChange { get; set; }
}