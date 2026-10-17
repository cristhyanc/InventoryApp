using System.Text.Json.Serialization;

using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One range of provider data a fully successful run or a committed file accounted for (issue #730,
/// parent #329 decision P9).
///
/// <para>Nothing in the application could say this before. The sales synchronization reads a rolling
/// <c>lastSales</c> window per machine and settlement imports are recorded per file, so a report
/// looking at an empty period could not tell "this business sold nothing" from "nothing was ever
/// read for this period" - and it rendered both as <c>0</c>. A row here is the evidence that makes
/// the difference, and the claim it makes is exact: every provider record of this
/// <see cref="Provider"/> and <see cref="Source"/> whose instant falls in
/// <c>[FromUtc, ToUtc)</c> is stored for this business.</para>
///
/// <para><strong>Append-only, and written only for work that fully succeeded.</strong> A run that
/// failed, was cancelled, failed for one machine or could not account for every row it read writes
/// no row at all; the gap that leaves is what a report then shows as unverified. Nothing updates or
/// deletes a row, because a coverage record is a historical statement about what was retrieved, not
/// a current status - and overlapping rows are normal, since each successful run claims its own
/// window and the availability policy merges them.</para>
///
/// <para>Ordinary tenant-owned data: the owning business is stamped by
/// <c>BusinessOwnershipEnforcer</c> and read back through the query filters, never supplied by a
/// request (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public class ProviderDataCoverage : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>The external system the data came from.</summary>
    public ExternalProvider Provider { get; set; }

    /// <summary>
    /// Which route brought it in. It is part of the record rather than implied, because the three
    /// routes account for different things and a report figure reads only the sources that feed it.
    /// </summary>
    public ProviderDataSource Source { get; set; }

    /// <summary>The inclusive start of the covered range, as a UTC instant.</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>
    /// The exclusive end of the covered range, as a UTC instant. Half-open, so two consecutive runs
    /// that meet exactly leave no uncovered instant between them.
    /// </summary>
    public DateTime ToUtc { get; set; }

    /// <summary>
    /// When the run or import that recorded this range completed. It is provenance, deliberately
    /// kept separate from the range itself: a settlement file imported in October can cover August,
    /// and an import instant must never be read as a coverage boundary.
    /// </summary>
    public DateTime RecordedAtUtc { get; set; }
}
