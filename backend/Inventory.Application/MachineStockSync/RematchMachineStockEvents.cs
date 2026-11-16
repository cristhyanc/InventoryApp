using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One machine's current MDB mapping, read once and applied to many events (issue #1248): the
/// Nayax product each MDB position reports, and the local product that carries each of those Nayax
/// ids. It is the exact input <see cref="NayaxMachineStockMatchPolicy"/> resolves an alert with at
/// import, held as a value so an import and a re-match of an older event are decided by the same
/// mapping read in the same breath, and never by two reads that disagree.
/// </summary>
public sealed class MachineStockMatchRefresh
{
    private readonly long _nayaxMachineId;
    private readonly IReadOnlyDictionary<int, long> _nayaxProductIdByMdb;
    private readonly IReadOnlyDictionary<long, NayaxStockSyncProduct> _localProductsByNayaxId;

    public MachineStockMatchRefresh(
        long nayaxMachineId,
        IReadOnlyDictionary<int, long> nayaxProductIdByMdb,
        IReadOnlyDictionary<long, NayaxStockSyncProduct> localProductsByNayaxId)
    {
        _nayaxMachineId = nayaxMachineId;
        _nayaxProductIdByMdb = nayaxProductIdByMdb;
        _localProductsByNayaxId = localProductsByNayaxId;
    }

    /// <summary>How this mapping resolves a freshly parsed alert. The import's own resolution.</summary>
    public NayaxStockEventResolution Resolve(ParsedNayaxStockAdjustment parsed) =>
        NayaxMachineStockMatchPolicy.Resolve(
            _nayaxMachineId, parsed, _nayaxProductIdByMdb, _localProductsByNayaxId);

    /// <summary>
    /// How this mapping resolves a stored event, or <see langword="null"/> when there is nothing to
    /// resolve it by: an alert whose EventData never parsed has no MDB and no product name, and
    /// parsing the same stored text again fails the same way, so it is left exactly as it is.
    /// </summary>
    public NayaxStockEventResolution? Resolve(MachineStockEventState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state is { ParsedMdb: int mdb, ParsedProductName: { Length: > 0 } name }
            ? Resolve(new ParsedNayaxStockAdjustment(mdb, name, state.ParsedQuantity ?? 0))
            : null;
    }
}

/// <summary>
/// Re-evaluates the product match of Sync Restock events that are still waiting for one (issue
/// #1248).
///
/// <para><strong>Why an event needs re-matching at all.</strong> An alert's match is decided once,
/// at import, from the machine's MDB positions and the local product carrying each position's Nayax
/// id. When either was missing at that moment the event became Needs Review - and stayed there for
/// ever, even after the operator mapped the position in Nayax or recorded the Nayax id on the local
/// product, so the refill had to be entered by hand. The match is therefore re-evaluated whenever
/// the Sync Restock list loads and again, under the write lock, when an event is applied.</para>
///
/// <para><strong>Only events that are still waiting, and only their match columns.</strong> An
/// event that was applied, reconciled as already recorded manually or applied as a separate restock
/// is never re-matched: its match is the record of which product's storage actually moved.
/// <see cref="IsWaitingForAMatch"/> is that rule, used by the read that finds candidates and again
/// at the write, and what is written is the three match columns and nothing else.</para>
///
/// <para><strong>It changes no rule.</strong> The resolution is
/// <see cref="NayaxMachineStockMatchPolicy"/>'s, unchanged: Machine + MDB is still the mapping, the
/// product name is still only a tolerant validation check, an unresolvable event is still Needs
/// Review with a reason, and nothing here guesses a product.</para>
/// </summary>
public sealed class RematchMachineStockEvents
{
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IMachineStockEventStore _store;

    public RematchMachineStockEvents(
        INayaxLynxClient nayax, INayaxProductLinkStore productLinks, IMachineStockEventStore store)
    {
        _nayax = nayax;
        _productLinks = productLinks;
        _store = store;
    }

    /// <summary>
    /// Whether an event is one a refreshed mapping may still decide: unprocessed, with no duplicate
    /// resolution recorded, not matched to a local product, and with an MDB and a product name to
    /// be matched by at all - an alert whose EventData never parsed has neither, and parsing the
    /// same stored text again fails the same way.
    /// </summary>
    public static bool IsWaitingForAMatch(MachineStockEventState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed
            && state.DuplicateResolution == NayaxDuplicateResolution.None
            && state.MatchStatus != NayaxStockEventMatchStatus.Matched
            && state.ParsedMdb is not null
            && !string.IsNullOrEmpty(state.ParsedProductName);
    }

    /// <summary>
    /// The machine's mapping as Nayax reports it now. It is read fresh (issue #842), never from the
    /// 60-second machine-products cache, for the same reason the import reads it fresh: this
    /// mapping decides which product's storage a stock adjustment is recorded against.
    /// </summary>
    public async Task<MachineStockMatchRefresh> ReadCurrentMappingAsync(
        long nayaxMachineId, CancellationToken cancellationToken)
    {
        var machineProducts = await _nayax
            .GetMachineProductsFreshAsync(nayaxMachineId, cancellationToken)
            .ConfigureAwait(false);

        var nayaxProductIdByMdb = machineProducts
            .Where(product => product.MDBCode.HasValue && product.NayaxProductID.HasValue)
            .GroupBy(product => product.MDBCode!.Value)
            .ToDictionary(group => group.Key, group => group.First().NayaxProductID!.Value);

        // The Nayax ids the machine reported, resolved to the local products they name through the
        // product row's own external id; the storage read that follows is given local ids only.
        var localProductIdByNayaxId = await _productLinks
            .GetLocalProductIdsAsync(nayaxProductIdByMdb.Values.Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);
        var storageProducts = await _store
            .GetStorageProductsAsync(localProductIdByNayaxId.Values.Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);

        // Re-keyed by Nayax id for the Domain policy, which resolves MDB -> Nayax product -> local
        // product. A Nayax id with no link, or whose linked product the storage read did not
        // return, is simply absent, which is the policy's "no local product is mapped" branch.
        var localProducts = localProductIdByNayaxId
            .Where(link => storageProducts.ContainsKey(link.Value))
            .ToDictionary(link => link.Key, link => storageProducts[link.Value]);

        return new MachineStockMatchRefresh(nayaxMachineId, nayaxProductIdByMdb, localProducts);
    }

    /// <summary>
    /// Records the refreshed match of <paramref name="states"/> and returns how many changed. The
    /// caller passes the states it read, because the apply reads them inside its own write lock and
    /// must write from that read; the Sync Restock list reads them through
    /// <see cref="IMachineStockEventStore.FindRematchCandidatesAsync"/> before the import.
    /// </summary>
    public Task<int> RefreshAsync(
        long nayaxMachineId,
        IReadOnlyList<MachineStockEventState> states,
        MachineStockMatchRefresh mapping,
        CancellationToken cancellationToken) =>
        _store.UpdateMatchesAsync(nayaxMachineId, Changes(states, mapping), cancellationToken);

    /// <summary>
    /// The refreshed matches of <paramref name="states"/> that differ from what is stored, for a
    /// caller that has already read those states - the apply, which reads them inside its own write
    /// lock and must decide and write from that read rather than a second one. A state that is not
    /// waiting for a match, one with nothing to match on, and one the mapping resolves exactly as
    /// it is already stored each contribute nothing.
    /// </summary>
    public static IReadOnlyList<MachineStockEventMatchUpdate> Changes(
        IEnumerable<MachineStockEventState> states, MachineStockMatchRefresh mapping)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(mapping);

        List<MachineStockEventMatchUpdate> changes = [];
        foreach (var state in states)
        {
            if (!IsWaitingForAMatch(state) || mapping.Resolve(state) is not { } resolution)
                continue;

            if (resolution.MatchStatus == state.MatchStatus
                && resolution.MatchedProductId == state.MatchedProductId
                && resolution.NeedsReviewReason == state.NeedsReviewReason)
            {
                continue;
            }

            changes.Add(new MachineStockEventMatchUpdate(
                state.Id, resolution.MatchStatus, resolution.MatchedProductId, resolution.NeedsReviewReason));
        }

        return changes;
    }
}
