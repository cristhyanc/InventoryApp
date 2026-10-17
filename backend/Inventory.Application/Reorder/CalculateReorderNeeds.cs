using System.Collections.Concurrent;
using Inventory.Application.Nayax;

namespace Inventory.Application.Reorder;

/// <summary>
/// The reorder-alert aggregation use case (issue #47): fetches the live machine fleet and, for each
/// machine, its current Nayax product mappings through the Application-owned <see cref="INayaxLynxClient"/>
/// port, summing <c>MissingStockByMDB</c> per catalogue product ID exactly as the legacy
/// <c>InventoryApi.Services.ProductService.LowStock</c> loop did, and combines it with outstanding
/// supplier-order quantity from <see cref="IOutstandingSupplierOrderQuantityStore"/>. It has no
/// knowledge of which products a caller's search/category/supplier filter selected: both dictionaries
/// are keyed by product ID over the whole catalogue/machine fleet, so a caller looks up only the
/// products it cares about.
///
/// The product ID in both of them is the <em>local</em> one, resolved from the selection's
/// <c>NayaxProductID</c> through <see cref="INayaxProductLinkStore"/> (issue #733) rather than used
/// as a local id directly. A selection whose Nayax product this business holds no link for carries
/// no product id at all, which is the same thing a selection with no <c>NayaxProductID</c> already
/// carries and is treated identically everywhere downstream: it is not summed into any product's
/// replenishment need and the Domain refill rules skip it as unmapped.
///
/// Issue #459 additionally keeps the individual machine selections that read returned
/// (<see cref="ReorderNeedsResult.MachineSelections"/>) and the machines it covered
/// (<see cref="ReorderNeedsResult.MachineIds"/>). Nothing about the reorder aggregation changes: they
/// are the same payload the per-product totals are summed from, retained so the home Dashboard's
/// refill and ordering cards can both be answered from this one fleet read instead of a second
/// <c>GetMachineProductsAsync</c> fan-out.
///
/// The per-machine <c>GetMachineProductsAsync</c> calls run with bounded parallelism
/// (<see cref="MaxConcurrentMachineRequests"/>) rather than one request per machine in sequence or an
/// unbounded fan-out, chosen over a cache/snapshot because the current machine fleet is small and a
/// snapshot would add freshness/invalidation semantics the issue asks to avoid absent clear evidence
/// bounded parallelism is unsuitable. A failing machine-product request (typed as
/// <c>Inventory.Infrastructure.Nayax.NayaxUpstreamException</c> at the HTTP adapter) or caller
/// cancellation propagates out of <see cref="Handle"/> unchanged rather than being absorbed into a
/// partial result, so a partial aggregate is never presented as a complete one.
///
/// <para><b>Two explicit modes, because a sales provider is optional</b> (issue #726, under #329).
/// Stock-storage management is this application's own purpose: reorder alerts are about storage
/// stock, thresholds, restock targets and outstanding supplier orders, and only the machines'
/// current replenishment need comes from Nayax. So this use case answers in
/// <see cref="ReorderMode.MachineAware"/> when it accounted for the whole fleet, and in
/// <see cref="ReorderMode.StorageOnly"/> - with the reason - when the fleet is unavailable to this
/// business, instead of failing the request and taking the local sections of the Dashboard, the
/// reorder-alert list and Needs Ordering down with it.</para>
///
/// <para><b>Exactly two answers degrade, and both are availability answers about a feature</b>
/// (issue #329, decision P9), which is the same line <c>GetMachineStockReconciliation</c> already
/// draws: <see cref="NayaxNotConnectedException"/> (the business has no credentials an ordinary
/// operation may use - nothing stored, <c>NotConfigured</c>, or the <c>NeedsAttention</c> the status
/// gate refuses) and <see cref="NayaxPermissionNotGrantedException"/> (the credentials are usable and
/// Nayax refused this read for lack of permission). Availability is per feature, so a 403 on either
/// fleet read makes <em>reorder</em> storage-only and says so, and still writes no connection
/// status.</para>
///
/// <para><b>Everything else is still an error, including mid-fleet</b> (issue #726). A transport
/// failure, a cancellation, a <c>401</c> that closes the gate part-way through the fan-out and the
/// support-session refusal (<c>SupportSessionNayaxUnavailableException</c>, issue #551) all
/// propagate: a fleet that was half read is an outage, not a smaller fleet, and must never be
/// published as a complete machine-aware result or quietly relabelled storage-only. The mode is
/// decided before the fan-out, from the fleet read, and only a permission refusal can change it
/// afterwards.</para>
/// </summary>
public sealed class CalculateReorderNeeds
{
    /// <summary>
    /// The upper bound on simultaneous live <c>GetMachineProductsAsync</c> requests. A fixed
    /// engineering constant, not environment configuration: the current machine fleet is small enough
    /// that this is chosen for safety headroom against Nayax rate limits, not for throughput tuning.
    /// </summary>
    public const int MaxConcurrentMachineRequests = 4;

    private readonly INayaxLynxClient _nayax;
    private readonly IOutstandingSupplierOrderQuantityStore _outstandingOrders;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly int _maxConcurrentMachineRequests;

    public CalculateReorderNeeds(
        INayaxLynxClient nayax,
        IOutstandingSupplierOrderQuantityStore outstandingOrders,
        INayaxProductLinkStore productLinks)
        : this(nayax, outstandingOrders, productLinks, MaxConcurrentMachineRequests)
    {
    }

    /// <summary>
    /// Overload for a caller that needs a specific concurrency bound (tests proving the limit is
    /// respected). DI resolves the three-argument constructor above, since this one's
    /// <paramref name="maxConcurrentMachineRequests"/> has no registered service to satisfy it.
    /// </summary>
    public CalculateReorderNeeds(
        INayaxLynxClient nayax,
        IOutstandingSupplierOrderQuantityStore outstandingOrders,
        INayaxProductLinkStore productLinks,
        int maxConcurrentMachineRequests)
    {
        if (maxConcurrentMachineRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMachineRequests), "Must be at least 1.");

        _nayax = nayax;
        _outstandingOrders = outstandingOrders;
        _productLinks = productLinks;
        _maxConcurrentMachineRequests = maxConcurrentMachineRequests;
    }

    public async Task<ReorderNeedsResult> Handle(CancellationToken cancellationToken)
    {
        List<NayaxMachine> machines;
        try
        {
            machines = await _nayax.GetMachinesAsync(cancellationToken);
        }
        catch (NayaxNotConnectedException)
        {
            // No usable connection for this business, and no request reached Nayax: the credential
            // provider refuses before the stored connection is read.
            return await StorageOnlyAsync(MachineNeedUnavailableReason.NayaxNotConnected, cancellationToken);
        }
        catch (NayaxPermissionNotGrantedException)
        {
            return await StorageOnlyAsync(MachineNeedUnavailableReason.PermissionNotGranted, cancellationToken);
        }

        // The individual selections behind the per-product totals below, kept from the same read so
        // a caller that needs the machines' current stock levels - the home Dashboard's refill card
        // (issue #459) - does not fan out across the fleet a second time. A concurrent collection,
        // because the fan-out below writes to it from several machine requests at once. They still
        // carry the remote identity at this point: nothing local has been resolved yet.
        var fleetSelections = new ConcurrentBag<FleetSelection>();

        // Set by whichever machine request Nayax refuses for lack of permission, and read after the
        // fan-out has completed. A refusal is not thrown out of the loop body, deliberately: a
        // permission answer is not a failure, and letting it fault the parallel loop would wrap it
        // beside the sibling cancellations the loop then raises, leaving the caller to pick the
        // availability answer out of an aggregate. It is read at the top of the body too, so the
        // remaining machines are skipped rather than refused one by one - a 403 is the token's
        // scopes, so every one of them would be refused the same way.
        var permissionRefused = false;

        await Parallel.ForEachAsync(
            machines,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (machine, machineCancellationToken) =>
            {
                if (Volatile.Read(ref permissionRefused))
                    return;

                List<NayaxMachineProduct> machineProducts;
                try
                {
                    machineProducts = await _nayax.GetMachineProductsAsync(machine.MachineID, machineCancellationToken);
                }
                catch (NayaxPermissionNotGrantedException)
                {
                    Volatile.Write(ref permissionRefused, true);
                    return;
                }

                foreach (var machineProduct in machineProducts)
                {
                    // The machine the request was made for, not the payload's own nullable MachineID
                    // field, so a selection is always attributable to a machine.
                    fleetSelections.Add(new FleetSelection(
                        machine.MachineID,
                        machineProduct.NayaxProductID,
                        machineProduct.PAR ?? 0,
                        machineProduct.MissingStockByMDB ?? 0,
                        machineProduct.VendOutAlertThreshold ?? 0));
                }
            });

        if (permissionRefused)
        {
            // Everything read before the refusal is discarded rather than reported: a fleet this
            // business is not allowed to read is a storage-only answer, never a partial one.
            return await StorageOnlyAsync(MachineNeedUnavailableReason.PermissionNotGranted, cancellationToken);
        }

        // Read after the fan-out has completed, never beside it: this and the outstanding-order read
        // below land on the request's own scoped AppDbContext, which supports one operation at a time.
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            fleetSelections
                .Where(selection => selection.NayaxProductId.HasValue)
                .Select(selection => selection.NayaxProductId!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        // Ordered, so the same fleet always produces the same result whatever order the concurrent
        // requests happened to complete in.
        var selections = fleetSelections
            .Select(selection => new MachineSelectionStock(
                selection.MachineId,
                LocalProductId(selection.NayaxProductId, localProductIdByNayaxId),
                selection.Par,
                selection.MissingStockByMdb,
                selection.VendOutAlertThreshold))
            .OrderBy(selection => selection.MachineId)
            .ThenBy(selection => selection.ProductId)
            .ToList();

        // Summed over the resolved selections, so a selection naming a Nayax product this business
        // has no link for contributes to no product's replenishment need - exactly as a selection
        // with no NayaxProductID already contributed to none.
        var machineReplenishmentNeedByProductId = selections
            .Where(selection => selection.ProductId.HasValue)
            .GroupBy(selection => selection.ProductId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(selection => selection.MissingStockByMdb));

        var onOrderQuantityByProductId = await _outstandingOrders.GetOutstandingQuantitiesByProductAsync(cancellationToken);

        return new ReorderNeedsResult(machineReplenishmentNeedByProductId, onOrderQuantityByProductId)
        {
            Mode = ReorderMode.MachineAware,
            MachineIds = machines.Select(machine => machine.MachineID).Distinct().Order().ToList(),
            MachineSelections = selections,
        };
    }

    private static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;

    /// <summary>
    /// One machine selection as the live fleet read returned it, before any local identity is
    /// attached. Private, and separate from <see cref="MachineSelectionStock"/>, so a remote
    /// <c>NayaxProductID</c> can never leave this use case in a field callers read as a local
    /// product id.
    /// </summary>
    private readonly record struct FleetSelection(
        long MachineId, long? NayaxProductId, int Par, int MissingStockByMdb, int VendOutAlertThreshold);

    /// <summary>
    /// The storage-only answer: the outstanding supplier-order quantities, which are a local fact and
    /// are read exactly as they are in machine-aware mode, and no machine need at all.
    /// </summary>
    private async Task<ReorderNeedsResult> StorageOnlyAsync(
        MachineNeedUnavailableReason reason, CancellationToken cancellationToken) =>
        ReorderNeedsResult.StorageOnly(
            await _outstandingOrders.GetOutstandingQuantitiesByProductAsync(cancellationToken), reason);
}
