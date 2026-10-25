using Inventory.Application.PlatformOptions;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// In-memory fake of the option-visibility port (issue #873), so the Application tests exercise the
/// default rule and the write without EF Core or a database.
///
/// <para>It starts with <em>no stored setting at all</em>, which is the shipped state of every
/// installation: that is the case the default rule exists for, so it is the case a test gets unless
/// it asks for another.</para>
/// </summary>
public sealed class FakeOptionVisibilityStore : IOptionVisibilityStore
{
    private OptionVisibilitySettings? _stored;

    public FakeOptionVisibilityStore()
    {
    }

    private FakeOptionVisibilityStore(OptionVisibilitySettings stored)
    {
        _stored = stored;
    }

    /// <summary>A store that already holds an answer, as one a Super Admin has switched does.</summary>
    public static FakeOptionVisibilityStore Holding(bool historicalGstClassificationVisible) =>
        new(new OptionVisibilitySettings(historicalGstClassificationVisible));

    /// <summary>Every write this store received, in order, so a refusal can be shown to write nothing.</summary>
    public List<OptionVisibilitySettings> Writes { get; } = [];

    /// <summary>The instant of the last write, which the use case takes from the clock.</summary>
    public DateTime? LastUpdatedAtUtc { get; private set; }

    public Task<OptionVisibilitySettings?> FindAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_stored);

    public Task<OptionVisibilitySettings> SetHistoricalGstClassificationVisibleAsync(
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        _stored = new OptionVisibilitySettings(visible);
        Writes.Add(_stored);
        LastUpdatedAtUtc = updatedAtUtc;
        return Task.FromResult(_stored);
    }
}

/// <summary>Collects the option-visibility audit entries a use case emitted (issue #873).</summary>
public sealed class FakeOptionVisibilityAudit : IOptionVisibilityAudit
{
    public List<OptionVisibilityAuditEntry> Entries { get; } = [];

    public void Record(OptionVisibilityAuditEntry entry) => Entries.Add(entry);
}
