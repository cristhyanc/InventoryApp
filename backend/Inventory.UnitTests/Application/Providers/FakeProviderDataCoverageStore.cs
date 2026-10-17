using Inventory.Application.Providers;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.Availability;

namespace InventoryApi.Tests.Application.Providers;

/// <summary>
/// In-memory fake of the provider coverage write port (issue #730), so a test can assert exactly
/// what a run or an import claimed to have accounted for - including, in most of the cases the
/// issue names, that it claimed nothing at all.
///
/// It applies the same empty-range rule the real adapter does, so a test cannot see a row the
/// database would have refused to store.
/// </summary>
public sealed class FakeProviderDataCoverageStore : IProviderDataCoverageStore
{
    public List<RecordedCoverage> Recorded { get; } = [];

    public Task RecordAsync(
        ExternalProvider provider,
        ProviderDataSource source,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        if (toUtc > fromUtc)
            Recorded.Add(new RecordedCoverage(provider, source, fromUtc, toUtc));
        return Task.CompletedTask;
    }

    /// <summary>Every range recorded for one source, in the order it was recorded.</summary>
    public IReadOnlyList<ReportCoverageRange> RangesFor(ProviderDataSource source) =>
        Recorded.Where(x => x.Source == source)
            .Select(x => new ReportCoverageRange(x.FromUtc, x.ToUtc))
            .ToList();

    public sealed record RecordedCoverage(
        ExternalProvider Provider, ProviderDataSource Source, DateTime FromUtc, DateTime ToUtc);
}
