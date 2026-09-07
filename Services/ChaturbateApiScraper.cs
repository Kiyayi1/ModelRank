using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ModelRank.Models;

namespace ModelRank.Services;

// Chaturbate's room list is rendered from a JSON API (see room-list/?limit=&offset=) that the
// site's own frontend calls to page through listings. Hitting it directly (no browser, no DOM
// wait, no per-page delay) is both far faster and far more consistent than the old Playwright
// walk: the listing reorders continuously as viewer counts change, so a slow serial scan could
// scan page 3 just as the target model moved from page 5 to page 1, missing it entirely for that
// cycle. Two things fix that here:
//   1. Locality: models drift, they don't teleport, between short polling intervals. So we check
//      near the offset we found them at last time before anything else — usually a single request.
//   2. Speed: if that fails (they went offline and re-entered, or we have no history yet), the
//      whole listing is swept with many requests in flight at once instead of one at a time. That
//      shrinks the window in which the list can reorder out from under the scan from tens of
//      seconds down to roughly one round trip.
public class ChaturbateApiScraper : ISiteScraper
{
    public bool RequiresBrowser => false;
    public bool SupportsBulkFetch => true;

    private const int PageSize = 100; // API rejects anything above this
    private const int MaxConcurrency = 15;
    private const string ListUrl = "https://chaturbate.com/api/ts/roomlist/room-list/";

    private static readonly HttpClient _http = BuildClient();

    // Shared across every concurrent tracker, not per-call: this scraper is a singleton and
    // multiple models can now be monitored on Chaturbate at once (see MonitoringService), each
    // running its own sweep. A semaphore created inside FindModelRankAsync would only cap
    // concurrency *within* one model's sweep, so N trackers each sweeping at once would still
    // fire up to MaxConcurrency * N requests simultaneously — enough to get this IP rate-limited
    // (429) by Chaturbate's API, which was observed happening in practice once multiple trackers
    // ran together. Gating every request through one process-wide semaphore keeps the real
    // ceiling at MaxConcurrency no matter how many models are being tracked.
    private static readonly SemaphoreSlim _globalRequestGate = new(MaxConcurrency);

    // Remembers roughly where each model was last found so the next poll can probe there first
    // instead of re-scanning from the top of a list that reshuffles every few seconds.
    private static readonly ConcurrentDictionary<string, int> _lastKnownOffset = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient BuildClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<List<string>> FindModelRankAsync(IPage? page, string modelName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        void Log(string msg)
        {
            output.Add(msg);
            progress?.Report(msg);
            Debug.WriteLine($"[ChaturbateApi] {msg}");
        }

        Log($"Starting API search for '{modelName}'.");

        // --- Fast path: probe near the last known offset first ---------------------------
        if (_lastKnownOffset.TryGetValue(modelName, out var lastOffset))
        {
            Log($"Checking near last known position (offset {lastOffset})...");
            foreach (var probe in new[] { lastOffset, Math.Max(0, lastOffset - PageSize), lastOffset + PageSize })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (probed, _) = await FetchPageAsync(probe, cancellationToken);
                var probeHit = probed == null ? null : FindInRooms(probed.Rooms, modelName, probe);
                if (probeHit != null)
                {
                    _lastKnownOffset[modelName] = probe;
                    Log(FormatFound(probeHit));
                    return output;
                }
            }
            Log("Not near last known position, falling back to a full sweep.");
        }

        // --- Full sweep: page 0 first (also tells us the current total), then every -------
        // --- remaining page concurrently, bounded, cancelling the rest once found. --------
        // This single request is a point of failure for the whole cycle, so it gets a couple
        // of retries — one dropped connection or transient timeout shouldn't blank out a poll.
        RoomListResponse? first = null;
        string? lastError = null;
        for (int attempt = 1; attempt <= 3 && first == null; attempt++)
        {
            (first, lastError) = await FetchPageAsync(0, cancellationToken);
            if (first == null && attempt < 3)
            {
                Log($"Room list API request failed (attempt {attempt}/3): {lastError}. Retrying...");
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }
        if (first == null)
        {
            Log($"Could not reach the room list API after 3 attempts: {lastError}");
            return output;
        }

        var directHit = FindInRooms(first.Rooms, modelName, 0);
        if (directHit != null)
        {
            _lastKnownOffset[modelName] = 0;
            Log(FormatFound(directHit));
            return output;
        }

        int totalCount = first.TotalCount;
        var remainingOffsets = new List<int>();
        for (int o = PageSize; o < totalCount; o += PageSize)
            remainingOffsets.Add(o);

        Log($"Sweeping {remainingOffsets.Count + 1} pages ({totalCount} models live) with up to {MaxConcurrency} requests in flight...");

        using var sweepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RoomHit? hit = null;
        var hitLock = new object();

        // No per-sweep semaphore here: _globalRequestGate (shared by every tracker) already
        // bounds how many of these requests can be in flight at once app-wide.
        var tasks = remainingOffsets.Select(async offset =>
        {
            try
            {
                if (sweepCts.IsCancellationRequested) return;
                var (result, _) = await FetchPageAsync(offset, sweepCts.Token);
                if (result == null) return;
                var candidate = FindInRooms(result.Rooms, modelName, offset);
                if (candidate != null)
                {
                    lock (hitLock) { hit ??= candidate; }
                    sweepCts.Cancel(); // stop the rest of the in-flight sweep, we're done
                }
            }
            catch (OperationCanceledException) { }
        });

        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

        if (hit != null)
        {
            _lastKnownOffset[modelName] = hit.Offset;
            Log(FormatFound(hit));
        }
        else
        {
            // Note: unlike the old page-by-page scraper, a miss here already means the ENTIRE
            // current listing was swept — there's no partial-scan "restart immediately" case to
            // signal, so we deliberately don't emit "LAST_PAGE_REACHED". MonitoringService treats
            // that token as "skip the wait and retry now"; reusing it here would make a genuine
            // miss (model offline) spin in a zero-delay retry loop against the API forever.
            _lastKnownOffset.TryRemove(modelName, out _);
            Log($"'{modelName}' not found across {totalCount} live rooms.");
        }

        return output;
    }

    // Fetches every live room once and returns all of them keyed by username, instead of
    // searching for a single target. This is what lets MonitoringService update every tracked
    // Chaturbate model from one shared poll: one sweep serves however many models are being
    // watched, rather than each model triggering its own full sweep.
    public async Task<IReadOnlyDictionary<string, RoomSnapshot>> FetchAllAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new List<string>();
        void Log(string msg)
        {
            output.Add(msg);
            progress?.Report(msg);
            Debug.WriteLine($"[ChaturbateApi] {msg}");
        }

        RoomListResponse? first = null;
        string? lastError = null;
        for (int attempt = 1; attempt <= 3 && first == null; attempt++)
        {
            (first, lastError) = await FetchPageAsync(0, cancellationToken);
            if (first == null && attempt < 3)
            {
                Log($"Room list API request failed (attempt {attempt}/3): {lastError}. Retrying...");
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }
        if (first == null)
        {
            Log($"Could not reach the room list API after 3 attempts: {lastError}");
            return new Dictionary<string, RoomSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var byUsername = new Dictionary<string, RoomSnapshot>(StringComparer.OrdinalIgnoreCase);
        void AddPage(List<RoomDto> rooms, int offset)
        {
            for (int i = 0; i < rooms.Count; i++)
                byUsername[rooms[i].Username] = ToSnapshot(rooms[i], offset, i);
        }
        AddPage(first.Rooms, 0);

        int totalCount = first.TotalCount;
        var remainingOffsets = new List<int>();
        for (int o = PageSize; o < totalCount; o += PageSize)
            remainingOffsets.Add(o);

        Log($"Sweeping {remainingOffsets.Count + 1} pages ({totalCount} models live) with up to {MaxConcurrency} requests in flight...");

        var pageLock = new object();
        var tasks = remainingOffsets.Select(async offset =>
        {
            var (result, _) = await FetchPageAsync(offset, cancellationToken);
            if (result == null) return;
            lock (pageLock) { AddPage(result.Rooms, offset); }
        });
        await Task.WhenAll(tasks);

        Log($"Collected {byUsername.Count} rooms across {remainingOffsets.Count + 1} pages.");
        return byUsername;
    }

    private static RoomSnapshot ToSnapshot(RoomDto room, int offset, int indexInPage) => new()
    {
        Username = room.Username,
        Viewers = room.NumUsers,
        Followers = room.NumFollowers,
        Tags = room.Tags,
        ImageUrl = room.Img,
        IsNew = room.IsNew,
        StartTimestamp = room.StartTimestamp,
        Label = room.Label,
        HasPassword = room.HasPassword,
        TokensRemaining = ExtractTokensRemaining(room.RoomSubject),
        Page = offset / PageSize + 1,
        Position = indexInPage + 1,
        Rank = offset + indexInPage + 1
    };

    private async Task<(RoomListResponse? Response, string? Error)> FetchPageAsync(int offset, CancellationToken cancellationToken)
    {
        await _globalRequestGate.WaitAsync(cancellationToken);
        try
        {
            var url = $"{ListUrl}?limit={PageSize}&offset={offset}";
            using var response = await _http.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Back off for however long the server asked (or a sane default), then give up on
                // this page for this cycle — retrying immediately just adds to the pile that got
                // us rate-limited in the first place.
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                Debug.WriteLine($"[ChaturbateApi] Offset {offset} got 429, backing off {retryAfter}.");
                await Task.Delay(retryAfter, cancellationToken);
                return (null, "429 Too Many Requests");
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<RoomListResponse>(cancellationToken: cancellationToken);
            return (body, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[ChaturbateApi] Fetch offset {offset} failed: {error}");
            return (null, error);
        }
        finally
        {
            _globalRequestGate.Release();
        }
    }

    private static RoomHit? FindInRooms(List<RoomDto>? rooms, string modelName, int offset)
    {
        if (rooms == null) return null;
        for (int i = 0; i < rooms.Count; i++)
        {
            if (string.Equals(rooms[i].Username, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return new RoomHit
                {
                    Username = rooms[i].Username,
                    Viewers = rooms[i].NumUsers,
                    Followers = rooms[i].NumFollowers,
                    Tags = rooms[i].Tags,
                    ImageUrl = rooms[i].Img,
                    IsNew = rooms[i].IsNew,
                    StartTimestamp = rooms[i].StartTimestamp,
                    Label = rooms[i].Label,
                    HasPassword = rooms[i].HasPassword,
                    TokensRemaining = ExtractTokensRemaining(rooms[i].RoomSubject),
                    Offset = offset,
                    Page = offset / PageSize + 1,
                    Position = i + 1,
                    Rank = offset + i + 1
                };
            }
        }
        return null;
    }

    // Matches the platform's auto-generated goal-progress text, e.g. "[458 tokens remaining]",
    // "[2036 tokens left]", "[1899 tks left]", "[316 tokens to goal]", or the older unbracketed
    // "4023 remaining to goal!" style. Verified against 401 live room subjects: 258 matched, and
    // the single miss was a broadcaster's own typo (a stray bracket splitting the number from the
    // word). Deliberately does NOT match bare numbers like "at 1111 tokens" (a goal threshold, not
    // a remaining count) or bracketed numbers with no accompanying keyword (too ambiguous).
    private static int? ExtractTokensRemaining(string roomSubject)
    {
        if (string.IsNullOrWhiteSpace(roomSubject)) return null;
        var match = Regex.Match(roomSubject, @"(\d+)\s*(?:tokens?|tks?)?\s*(?:remaining|left|to goal)", RegexOptions.IgnoreCase);
        if (match.Success) return int.Parse(match.Groups[1].Value);
        if (Regex.IsMatch(roomSubject, @"goal\s*reached", RegexOptions.IgnoreCase)) return 0;
        return null;
    }

    private static string FormatFound(RoomHit hit) =>
        $"Found '{hit.Username}' on page {hit.Page}, position {hit.Position} (overall rank: {hit.Rank}) | Viewers: {hit.Viewers} | Followers: {hit.Followers} | Tags: {string.Join(",", hit.Tags)} | Img: {hit.ImageUrl} | IsNew: {hit.IsNew} | StartTs: {hit.StartTimestamp} | Label: {hit.Label} | HasPassword: {hit.HasPassword}{(hit.TokensRemaining.HasValue ? $" | TokensRemaining: {hit.TokensRemaining}" : "")}";

    private class RoomHit
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
        public int Offset { get; set; }
        public int Page { get; set; }
        public int Position { get; set; }
        public int Rank { get; set; }
    }

    private class RoomListResponse
    {
        [JsonPropertyName("rooms")]
        public List<RoomDto> Rooms { get; set; } = new();

        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
    }

    private class RoomDto
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("num_users")]
        public long NumUsers { get; set; }

        [JsonPropertyName("num_followers")]
        public long NumFollowers { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("img")]
        public string Img { get; set; } = "";

        [JsonPropertyName("is_new")]
        public bool IsNew { get; set; }

        [JsonPropertyName("start_timestamp")]
        public long StartTimestamp { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("has_password")]
        public bool HasPassword { get; set; }

        [JsonPropertyName("room_subject")]
        public string RoomSubject { get; set; } = "";
    }
}
