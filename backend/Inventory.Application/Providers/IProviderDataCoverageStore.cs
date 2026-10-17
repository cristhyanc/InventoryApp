using Inventory.Domain.Providers;

namespace Inventory.Application.Providers;

/// <summary>
/// Narrow persistence port for the provider data coverage record (issue #730, parent #329 decision
/// P9): the evidence that a stated range of provider data was actually retrieved and committed.
///
/// <para>It exists because nothing in the application could previously say which period a sync or an
/// import covered - the sales synchronization reads a rolling window per machine, and settlement
/// imports are recorded per file - so a report could not tell "no sales" apart from "no sales data".
/// A coverage row is the difference, and that is why it is written in exactly one circumstance:
/// <strong>work that fully succeeded</strong>. A run that failed, was cancelled, failed for one
/// machine, or could not account for every row it read writes nothing at all, and the gap it leaves
/// is what the report then shows as unverified.</para>
///
/// <para>The range is half-open, <c>[fromUtc, toUtc)</c>, in the same UTC instant space the reports
/// filter their rows by, and the claim it makes is exact: every provider record whose instant falls
/// in it is now stored. The owning business is resolved centrally and stamped by the ownership
/// enforcer; no caller passes one (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public interface IProviderDataCoverageStore
{
    /// <summary>
    /// Records one coverage range for the current business. An empty or inverted range is not
    /// coverage and is ignored rather than stored, so a run that could claim nothing leaves no row
    /// that a later report would have to interpret.
    /// </summary>
    Task RecordAsync(
        ExternalProvider provider,
        ProviderDataSource source,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken);
}
