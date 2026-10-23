using System.Collections.Concurrent;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;

namespace Inventory.Application.PickList;

/// <summary>
/// The read-only Pick List projection use case (issue #221). For the selected machines it fetches
/// each machine's current live Nayax product mappings through the same
/// <see cref="INayaxLynxClient.GetMachineProductsFreshAsync"/> port and PAR/<c>MissingStockByMDB</c>
/// arithmetic <c>InventoryApi.Services.MachineService.GetMachineProducts</c> already uses for a single
/// machine's product list, aggregates them into a per-product/machine pick matrix, and compares the
/// combined pick quantity per product against its physical storage quantity from
/// <see cref="IPickListStorageStockStore"/>. It creates no inventory movement, refill, or persisted
/// state: it only reads.
///
/// Per-machine calls run with the same bounded parallelism
/// <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/> already established for fanning
/// out <c>GetMachineProductsAsync</c> across the machine fleet (issue #47), reused unchanged here
/// rather than re-deriving a concurrency bound for a second Nayax fan-out.
///
/// Each selection's <c>NayaxProductID</c> is resolved to a local product through
/// <see cref="INayaxProductLinkStore"/> (issue #733) before anything local is read; the storage
/// lookup, the matrix and the returned rows all carry local product ids only.
///
/// A product with no matching local catalogue row - one this business holds no Nayax link for, or
/// one outside the caller's business - is silently excluded from the result rather than appearing
/// with a fabricated zero storage quantity: both the link read and
/// <see cref="IPickListStorageStockStore"/> are scoped by the same tenant ownership every other
/// <c>AppDbContext</c> read is, so "not found" in either is the tenant boundary or an unmapped
/// selection, not a data gap to paper over.
///
/// A product the business has <em>retired</em> (<c>IsActive</c> false) is excluded too, and for a
/// stated reason rather than by absence (issue #736): deactivation is how a product with history is
/// taken out of use, so it stops appearing on the pick list exactly as it already stops raising a
/// reorder alert. Nothing about the product, its slots or its stock changes.
/// </summary>
public sealed class GetPickList
{
    private readonly INayaxLynxClient _nayax;
    private readonly IPickListStorageStockStore _storage;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly int _maxConcurrentMachineRequests;

    public GetPickList(
        INayaxLynxClient nayax, IPickListStorageStockStore storage, INayaxProductLinkStore productLinks)
        : this(nayax, storage, productLinks, CalculateReorderNeeds.MaxConcurrentMachineRequests)
    {
    }

    /// <summary>
    /// Overload for a caller that needs a specific concurrency bound (tests proving the limit is
    /// respected). DI resolves the three-argument constructor above, since this one's
    /// <paramref name="maxConcurrentMachineRequests"/> has no registered service to satisfy it.
    /// </summary>
    public GetPickList(
        INayaxLynxClient nayax,
        IPickListStorageStockStore storage,
        INayaxProductLinkStore productLinks,
        int maxConcurrentMachineRequests)
    {
        if (maxConcurrentMachineRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMachineRequests), "Must be at least 1.");

        _nayax = nayax;
        _storage = storage;
        _productLinks = productLinks;
        _maxConcurrentMachineRequests = maxConcurrentMachineRequests;
    }

    public async Task<PickListResult> Handle(IReadOnlyCollection<long> machineIds, CancellationToken cancellationToken)
    {
        var distinctMachineIds = machineIds.Distinct().ToList();
        if (distinctMachineIds.Count == 0)
            return new PickListResult(Array.Empty<PickListProduct>());

        // Keyed by the remote identity while the fan-out runs: nothing local has been resolved yet,
        // and the key is named for what it holds so it cannot be read as a product id.
        var cellsByProductAndMachine = new ConcurrentDictionary<(long NayaxProductId, long MachineId), PickListCell>();

        await Parallel.ForEachAsync(
            distinctMachineIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (machineId, machineCancellationToken) =>
            {
                // Fresh, never the cached selections (issue #842): the pick list is what an operator
                // loads a van from, so the quantity to pick has to be the machine's need now.
                var machineProducts = await _nayax.GetMachineProductsFreshAsync(
                    machineId, machineCancellationToken);
                foreach (var machineProduct in machineProducts)
                {
                    if (machineProduct.NayaxProductID is not long nayaxProductId)
                        continue;

                    var current = (machineProduct.PAR - machineProduct.MissingStockByMDB) ?? 0;
                    var target = machineProduct.PAR ?? 0;
                    var toPick = Math.Max(0, machineProduct.MissingStockByMDB ?? 0);

                    cellsByProductAndMachine.AddOrUpdate(
                        (nayaxProductId, machineId),
                        _ => new PickListCell(current, target, toPick, machineProduct.MDBCode),
                        (_, existing) => existing.Add(current, target, toPick, machineProduct.MDBCode));
                }
            });

        if (cellsByProductAndMachine.IsEmpty)
            return new PickListResult(Array.Empty<PickListProduct>());

        // Both reads land on the request's own scoped AppDbContext, so they are awaited one after
        // the other and only once the Nayax fan-out above has completed.
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            cellsByProductAndMachine.Keys.Select(key => key.NayaxProductId).Distinct().ToList(),
            cancellationToken);
        var storageProducts = await _storage.GetStorageProductsAsync(
            localProductIdByNayaxId.Values.Distinct().ToList(), cancellationToken);

        // A selection whose Nayax product has no link for this business is dropped here, the same
        // way one whose product is not in the caller's catalogue already was. One local product
        // never carries two Nayax links (the link table is unique on business/provider/product), so
        // regrouping on the resolved id cannot merge two different products' selections.
        //
        // A **retired** product is dropped for a different and deliberate reason (issue #736):
        // `IsActive` is how a product is taken out of use, and a product the business has retired is
        // not one an operator is sent to pick - the same rule the reorder alerts
        // (`ProductReorderPolicy.IsReorderAlert`), the site stock lists and the machine refill
        // alerts already apply. Its history, its machine slots and its stock are untouched; only
        // this projection stops naming it.
        var products = cellsByProductAndMachine
            .Where(cell => localProductIdByNayaxId.ContainsKey(cell.Key.NayaxProductId))
            .GroupBy(cell => localProductIdByNayaxId[cell.Key.NayaxProductId])
            .Where(group => storageProducts.ContainsKey(group.Key) && storageProducts[group.Key].IsActive)
            .Select(group =>
            {
                var storageProduct = storageProducts[group.Key];
                var machineQuantities = group
                    .OrderBy(cell => cell.Key.MachineId)
                    .Select(cell => new PickListMachineQuantity(
                        cell.Key.MachineId, cell.Value.MdbCode, cell.Value.CurrentQuantity, cell.Value.TargetQuantity, cell.Value.QuantityToPick))
                    .ToList();
                var totalQuantityToPick = machineQuantities.Sum(machineQuantity => machineQuantity.QuantityToPick);
                var storageShortageQuantity = Math.Max(0, totalQuantityToPick - storageProduct.QuantityInStock);

                // The lowest non-null per-machine code represents the row for sorting/display; Min()
                // over a nullable sequence ignores nulls and returns null only when every machine
                // quantity lacks a code, which is exactly the "missing code" case the row should fall
                // back to.
                var mdbCode = machineQuantities.Select(machineQuantity => machineQuantity.MdbCode).Min();

                return new PickListProduct(
                    group.Key,
                    storageProduct.ProductName,
                    mdbCode,
                    storageProduct.QuantityInStock,
                    totalQuantityToPick,
                    storageShortageQuantity,
                    machineQuantities);
            })
            // Ascending by MDB code - numeric, so 2 sorts before 10 - with missing codes grouped
            // deterministically (the default nullable comparer orders null before any value) and
            // ProductName as the tie-break for repeated/missing codes, matching the page's previous
            // sole ordering.
            .OrderBy(product => product.MdbCode)
            .ThenBy(product => product.ProductName, StringComparer.Ordinal)
            .ToList();

        return new PickListResult(products);
    }

    /// <summary>
    /// The accumulated current/target/pick figures for one product on one machine, summed across
    /// however many MDB slots that product occupies there - the same duplicate-mapping aggregation
    /// <see cref="CalculateReorderNeeds"/> already applies within a single machine.
    /// <see cref="MdbCode"/> is the first slot's code in Nayax's returned order; when more than one
    /// slot merges here, the later slots' codes are not kept separately, matching how their
    /// quantities are already merged rather than kept apart.
    /// </summary>
    private readonly record struct PickListCell(int CurrentQuantity, int TargetQuantity, int QuantityToPick, int? MdbCode)
    {
        public PickListCell Add(int current, int target, int toPick, int? mdbCode) =>
            new(CurrentQuantity + current, TargetQuantity + target, QuantityToPick + toPick, MdbCode ?? mdbCode);
    }
}
