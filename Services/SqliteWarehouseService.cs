using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ModelRank.Models;

namespace ModelRank.Services;

// A data warehouse for future reporting/dashboard use, kept deliberately separate from the
// JSON storage the live UI reads (JsonStorageService/IStorageService). Every poll writes a row
// here in addition to the JSON file — same source data, wider schema (includes fields the UI
// doesn't currently display, like session freshness and show type), queryable with real SQL.
public class SqliteWarehouseService : IWarehouseService
{
    private readonly string _connectionString;

    public SqliteWarehouseService()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "modelrank_warehouse.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    // Every column this schema is supposed to have. CREATE TABLE IF NOT EXISTS only helps on a
    // brand-new database — once the table already exists from an earlier run, it's a no-op, so
    // adding a column here later would otherwise be silently ignored and every insert
    // referencing it would fail (this happened once already: TokensRemaining/
    // TokensTippedThisPeriod were added to the schema and every write failed silently until this
    // migration step was added, because the on-disk table predated those columns). Initialize()
    // now diffs against PRAGMA table_info and ALTERs in whatever is missing, so future schema
    // additions stay safe without a manual migration step.
    private static readonly (string Name, string Type)[] Columns =
    [
        ("Site", "TEXT NOT NULL"),
        ("ModelName", "TEXT NOT NULL"),
        ("DisplayName", "TEXT"),
        ("Timestamp", "TEXT NOT NULL"),
        ("Page", "INTEGER"),
        ("Position", "INTEGER"),
        ("Rank", "INTEGER"),
        ("Viewers", "INTEGER"),
        ("Followers", "INTEGER"),
        ("Tags", "TEXT"),
        ("ImageUrl", "TEXT"),
        ("IsNew", "INTEGER"),
        ("SessionStartUtc", "TEXT"),
        ("Label", "TEXT"),
        ("HasPassword", "INTEGER"),
        ("TokensRemaining", "INTEGER"),
        ("TokensTippedThisPeriod", "INTEGER"),
    ];

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // WAL lets one writer and many readers proceed concurrently instead of blocking on the
        // whole-file lock that the default rollback journal uses; busy_timeout makes a writer
        // that does have to wait for another writer retry for up to 5s instead of throwing
        // SQLITE_BUSY immediately. Both matter once multiple trackers save results at once.
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragmaCommand.ExecuteNonQuery();
        }

        using (var createCommand = connection.CreateCommand())
        {
            var columnDefs = string.Join(",\n                ", Columns.Select(c => $"{c.Name} {c.Type}"));
            createCommand.CommandText = $"""
                CREATE TABLE IF NOT EXISTS search_results (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    {columnDefs}
                );
                """;
            createCommand.ExecuteNonQuery();
        }

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA table_info(search_results)";
            using var reader = pragmaCommand.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(reader.GetString(1)); // column index 1 = "name"
        }

        foreach (var (name, type) in Columns)
        {
            if (existingColumns.Contains(name)) continue;
            using var alterCommand = connection.CreateCommand();
            // SQLite ALTER TABLE ADD COLUMN doesn't allow a NOT NULL without a DEFAULT on an
            // existing table, so strip that constraint for migrated columns — existing rows
            // just get NULL there, which is correct (we don't know their historical value).
            var migratedType = type.Replace(" NOT NULL", "", StringComparison.OrdinalIgnoreCase);
            alterCommand.CommandText = $"ALTER TABLE search_results ADD COLUMN {name} {migratedType}";
            alterCommand.ExecuteNonQuery();
        }

        using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_search_results_model
                    ON search_results(Site, ModelName, Timestamp);
                """;
            indexCommand.ExecuteNonQuery();
        }
    }

    public async Task SaveResultAsync(SearchResult result)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA busy_timeout=5000;";
                await pragmaCommand.ExecuteNonQueryAsync();
            }
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_results
                    (Site, ModelName, DisplayName, Timestamp, Page, Position, Rank, Viewers,
                     Followers, Tags, ImageUrl, IsNew, SessionStartUtc, Label, HasPassword,
                     TokensRemaining, TokensTippedThisPeriod)
                VALUES
                    ($site, $modelName, $displayName, $timestamp, $page, $position, $rank, $viewers,
                     $followers, $tags, $imageUrl, $isNew, $sessionStartUtc, $label, $hasPassword,
                     $tokensRemaining, $tokensTipped)
                """;
            command.Parameters.AddWithValue("$site", result.Site.ToString());
            command.Parameters.AddWithValue("$modelName", result.ModelName);
            command.Parameters.AddWithValue("$displayName", result.DisplayName);
            command.Parameters.AddWithValue("$timestamp", result.Timestamp.ToString("o"));
            command.Parameters.AddWithValue("$page", result.Page);
            command.Parameters.AddWithValue("$position", result.Position);
            command.Parameters.AddWithValue("$rank", result.Rank);
            command.Parameters.AddWithValue("$viewers", ParseViewers(result.Viewers));
            command.Parameters.AddWithValue("$followers", result.Followers);
            command.Parameters.AddWithValue("$tags", string.Join(",", result.Tags));
            command.Parameters.AddWithValue("$imageUrl", result.ImageUrl);
            command.Parameters.AddWithValue("$isNew", result.IsNew ? 1 : 0);
            command.Parameters.AddWithValue("$sessionStartUtc", (object?)result.SessionStartUtc?.ToString("o") ?? DBNull.Value);
            command.Parameters.AddWithValue("$label", result.Label);
            command.Parameters.AddWithValue("$hasPassword", result.HasPassword ? 1 : 0);
            command.Parameters.AddWithValue("$tokensRemaining", (object?)result.TokensRemaining ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokensTipped", (object?)result.TokensTippedThisPeriod ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Warehouse writes are supplementary — never let a SQL hiccup interrupt live monitoring.
            Debug.WriteLine($"[SqliteWarehouseService] Failed to save result: {ex.Message}");
        }
    }

    private static int ParseViewers(string viewersText)
    {
        var digits = new string(viewersText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }
}
