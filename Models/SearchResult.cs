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
    public int Followers { get; set; }
    public List<string> Tags { get; set; } = new();
    public string ImageUrl { get; set; } = "";
    public bool IsNew { get; set; }
    public DateTime? SessionStartUtc { get; set; }        // when she went live this session, if known
    public string Label { get; set; } = "";               // show type, e.g. "public"
    public bool HasPassword { get; set; }
    public int? TokensRemaining { get; set; }              // from the goal-progress text in room_subject, if any
    public bool Found { get; set; }

    // UI-only trend indicators
    public int? PageChange { get; set; }
    public int? PositionChange { get; set; }
    public int? RankChange { get; set; }
    public int? ViewersChange { get; set; }
    public int? TokensTippedThisPeriod { get; set; }        // previous TokensRemaining minus this one; null if not computable
}