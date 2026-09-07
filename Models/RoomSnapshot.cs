namespace ModelRank.Models;

// One room's data from a site's bulk room-list fetch (see ISiteScraper.FetchAllAsync). Shared
// between the single-model lookup path and the all-models-at-once bulk path so both build results
// the same way.
public class RoomSnapshot
{
    public string Username { get; set; } = "";
    public long Viewers { get; set; }
    public long Followers { get; set; }
    public List<string> Tags { get; set; } = new();
    public string ImageUrl { get; set; } = "";
    public bool IsNew { get; set; }
    public long StartTimestamp { get; set; }
    public string Label { get; set; } = "";
    public bool HasPassword { get; set; }
    public int? TokensRemaining { get; set; }
    public int Page { get; set; }
    public int Position { get; set; }
    public int Rank { get; set; }
}
