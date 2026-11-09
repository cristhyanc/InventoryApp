using Inventory.Application.PlatformOptions;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// In-memory fake of the menu-visibility port (issue #1148), so the Application tests exercise the
/// default rule, the per-key write and the catalogue without EF Core or a database.
///
/// <para>It starts with <em>no stored row at all</em>, which is the state every installation is in
/// until a Super Admin switches something: that is the case the default rule exists for, so it is
/// the case a test gets unless it asks for another.</para>
/// </summary>
public sealed class FakeMenuVisibilityStore : IMenuVisibilityStore
{
    private readonly Dictionary<string, bool> _stored = new(StringComparer.Ordinal);

    public FakeMenuVisibilityStore()
    {
    }

    private FakeMenuVisibilityStore(params (string Key, bool Visible)[] rows)
    {
        foreach (var row in rows)
        {
            _stored[row.Key] = row.Visible;
        }
    }

    /// <summary>A store that already holds one answer, as one a Super Admin has switched does.</summary>
    public static FakeMenuVisibilityStore Holding(string key, bool visible) => new((key, visible));

    /// <summary>A store holding several answers, including keys a later release may have retired.</summary>
    public static FakeMenuVisibilityStore HoldingAll(params (string Key, bool Visible)[] rows) => new(rows);

    /// <summary>Every write this store received, in order, so a refusal can be shown to write nothing.</summary>
    public List<StoredMenuVisibility> Writes { get; } = [];

    /// <summary>The instant of the last write, which the use case takes from the clock.</summary>
    public DateTime? LastUpdatedAtUtc { get; private set; }

    public Task<IReadOnlyList<StoredMenuVisibility>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredMenuVisibility>>(
            [.. _stored.Select(row => new StoredMenuVisibility(row.Key, row.Value))]);

    public Task<bool> SetAsync(
        string key,
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        _stored[key] = visible;
        Writes.Add(new StoredMenuVisibility(key, visible));
        LastUpdatedAtUtc = updatedAtUtc;
        return Task.FromResult(visible);
    }
}

/// <summary>Collects the menu-visibility audit entries a use case emitted (issue #1148).</summary>
public sealed class FakeMenuVisibilityAudit : IMenuVisibilityAudit
{
    public List<MenuVisibilityAuditEntry> Entries { get; } = [];

    public void Record(MenuVisibilityAuditEntry entry) => Entries.Add(entry);
}
