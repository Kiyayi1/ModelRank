using ModelRank.Models;

namespace ModelRank.Services;

// Separate from IStorageService on purpose: the UI reads/writes through IStorageService (JSON)
// exactly as it always has. This is an additive, non-blocking write path that mirrors every
// saved result into SQL for future reporting/dashboard use — the UI never depends on it.
public interface IWarehouseService
{
    Task SaveResultAsync(SearchResult result);
}
