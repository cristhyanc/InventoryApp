using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>
/// Counts of one uploaded Nayax sales import; the <c>POST api/imports/nayax-sales</c> response
/// body, moved here from <c>InventoryApi.Services.Interfaces</c> with no JSON change (issue #301).
/// A row the import could not identify is counted as <paramref name="Skipped"/> and stays visible
/// rather than being silently dropped.
/// </summary>
public sealed record NayaxSalesImportResult(int Imported, int Updated, int Skipped);

/// <summary>
/// The uploaded Nayax sales import use case (issue #301, child 3 of 3 of #151), moved unchanged in
/// behaviour out of <c>InventoryApi.Services.ImportService.ImportNayaxSalesFromExcelAsync</c>: read
/// the uploaded export's rows through <see cref="INayaxSalesWorkbookReader"/>, persist each
/// identifiable row as a new or updated sale through <see cref="INayaxSalesImportStore"/>, cost it
/// through the Application <see cref="ICostSale"/> use case (issue #297), and replay the inventory
/// cost of every product a completed sale affected after its transition baseline through
/// <see cref="IRebuildProductCost"/> (issue #296).
///
/// Imported Nayax sales drive historical costing and every financial report, so the rules this
/// keeps are deliberate:
/// <list type="bullet">
///   <item>A row without a positive transaction identifier, a positive machine identifier and an
///   authorization time is skipped, never guessed at and never imported with a defaulted identity.</item>
///   <item>The sale instant follows the precedence <see cref="ResolveInstant"/> documents
///   (issue #380): a usable <c>AuthorizationDateTimeGMT</c> value is authoritative and may correct a
///   stored instant; without one, a stored sale keeps the instant it already has, and only a new
///   sale falls back to the export's own <c>MachineAuthorizationTime</c> column, whose timezone is
///   unverified because Nayax publishes no contract for the export. A GMT value the export carries
///   but that cannot be read never triggers that fallback.</item>
///   <item>A transaction this business already holds is updated in place rather than double
///   counted; a transaction another business holds is a new sale of this one, because a remote
///   <c>TransactionID</c> is unique only within the operator account that issued it.</item>
///   <item>An update never erases a historical cost the business already has: a row carrying no
///   transaction cost price leaves the stored one in place, and the cost actually applied to the
///   sale stays <see cref="ICostSale"/>'s decision with its recorded provenance.</item>
///   <item>Transaction statuses are classified by the Domain
///   <see cref="NayaxTransactionStatusClassifier"/> and products matched by the Domain
///   <see cref="ProductMatcher"/>; neither status identifiers nor matching rules are reimplemented
///   here.</item>
///   <item>An imported row's remote <c>NayaxProductID</c> names a local product only through the
///   provider id this business recorded on that product, read once per import through
///   <see cref="INayaxProductLinkStore"/> (issue #745). An id recorded on no product is an unknown
///   product, handled exactly as an unmatched id already was: the sale is still imported and stays
///   visibly uncosted, and no product's inventory cost is replayed for it. Nothing changes for data
///   that predates it, because issue #732's backfill recorded every product's Nayax id as the one it
///   was keyed by and issue #759's migration copied that onto the product row.</item>
///   <item><b>That answer is persisted, not re-derived</b> (issue #758, parent #329 decision A2c).
///   The product this import resolves is stored in <c>NayaxSales.ProductId</c> and is the one
///   association costing, the cost replay, the timestamp repair, the reports and the machine-stock
///   ledger read; <c>NayaxSales.NayaxProductId</c> keeps only the export's own raw product id, with
///   its provenance recorded. A row this import resolves to nothing is stored unresolved and stays
///   visible as such - no later reader rescues it by number or by name, so a product renamed or
///   duplicated after the import cannot move the sale.</item>
///   <item>The sales are saved first, the affected products' costs are replayed next, and the
///   replayed costs are saved last. A fatal data-quality failure in that replay therefore
///   propagates with the rebuilt costs unsaved, exactly as the legacy import behaved. The
///   per-product catch-and-continue handling the latest-sales synchronization gained in issue #362
///   is deliberately *not* applied here: that sync can never reconsider a sale it has already
///   stored, whereas re-uploading the export runs this import again, so failing loudly and
///   staging nothing is still the right answer. Whole-import atomicity is a separate change.</item>
///   <item><b>This import records no provider data coverage</b> (issue #730, parent #329 decision
///   P9). Importing every row of a workbook successfully says what was stored; it does not say that
///   the workbook held every transaction of the span those rows cover. The upload is a file a person
///   exported from Nayax Core, and it carries no statement of its own range or scope: it may have
///   been filtered to one machine, one payment type, one site or any other criterion, and the
///   authoritative <c>lastSales</c> contract carries no requested-period or scope metadata either
///   (verified against the Nayax developer portal: <c>GET /v1/machines/{MachineID}/lastSales</c>
///   takes only a machine id and returns no window boundary). Deriving a covered interval from the
///   earliest and latest instants it happens to contain would therefore claim completeness for time
///   nothing accounted for - precisely the false zero this coverage record exists to prevent. The
///   imported sales are reported in full and their period stays <c>Unverified</c> until a fully
///   successful synchronization covers it.</item>
/// </list>
/// </summary>
public sealed class ImportNayaxSales
{
    private readonly INayaxSalesWorkbookReader _workbook;
    private readonly INayaxSalesImportStore _store;
    private readonly ICostSale _saleCosting;
    private readonly IRebuildProductCost _inventoryCostRebuild;
    private readonly INayaxProductLinkStore _productLinks;

    public ImportNayaxSales(
        INayaxSalesWorkbookReader workbook,
        INayaxSalesImportStore store,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild,
        INayaxProductLinkStore productLinks)
    {
        _workbook = workbook;
        _store = store;
        _saleCosting = saleCosting;
        _inventoryCostRebuild = inventoryCostRebuild;
        _productLinks = productLinks;
    }

    /// <summary>
    /// Imports one uploaded transaction export and reports what happened to each of its rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The upload is not one of the supported formats. The message is the caller-facing one
    /// <c>ImportsController.ImportNayaxSales</c> already answers <c>400 Bad Request</c> with; it is
    /// deliberately not a <c>DomainValidationException</c>, because that would change this
    /// endpoint's response body from a bare JSON string to <c>ProblemDetails</c> - a contract change
    /// this migration must not make (docs/architecture.md § Domain and application error mapping
    /// records converting this action's exception handling as its own later change).
    /// </exception>
    /// <exception cref="InventoryCostDataQualityException">
    /// An affected product's cost history could not be replayed. The imported sales stay saved and
    /// the replayed costs stay unsaved, as described above.
    /// </exception>
    public async Task<NayaxSalesImportResult> Handle(
        NayaxSalesFileInput file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!NayaxSalesExportFormats.IsSupported(file.FileName))
            throw new InvalidOperationException(NayaxSalesExportFormats.UnsupportedMessage);

        IReadOnlyList<NayaxSalesImportRow> rows;
        using (var content = file.OpenReadStream())
        {
            rows = _workbook.Read(content, file.FileName);
        }

        // An export with no data rows reads nothing and writes nothing - not even the catalogue.
        if (rows.Count == 0)
            return new NayaxSalesImportResult(0, 0, 0);

        var candidates = await _store.GetProductCandidatesAsync(cancellationToken);

        // Sales whose two candidate products disagreed when issue #758 resolved the existing
        // history. Only an audited decision names one, so a re-import updates every other fact of
        // such a sale and leaves its product exactly as it is. Expected to be empty.
        var conflictingTransactionIds =
            (await _store.GetConflictingTransactionIdsAsync(cancellationToken)).ToHashSet();

        // The export's Nayax product ids are resolved to local product ids once, through the one
        // lookup over the recorded provider ids (issue #745, parent #329 decision A2b),
        // instead of being compared with Product.Id - which only ever worked because the local key
        // *is* the Nayax id. Awaited in sequence with the catalogue read above and the per-row reads
        // below: they all land on the request's own scoped context
        // (docs/architecture.md § Concurrency inside one request).
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            rows
                .Where(row => row.NayaxProductId.HasValue)
                .Select(row => row.NayaxProductId!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        var imported = 0;
        var updated = 0;
        var skipped = 0;

        // Each affected product mapped to the earliest completed sale that touched it, which is the
        // instant its cost replay has to restart from.
        var affected = new Dictionary<long, DateTime>();

        foreach (var row in rows)
        {
            if (row.TransactionId <= 0 || row.MachineId <= 0 ||
                (row.MachineAuthorizationTime is null && row.AuthorizationDateTimeGmtInput != NayaxSalesGmtInput.Valid))
            {
                skipped++;
                continue;
            }

            var stored = await _store.FindByTransactionIdAsync(row.TransactionId, cancellationToken);
            var instant = ResolveInstant(row, stored);
            if (instant is null)
            {
                skipped++;
                continue;
            }

            // The local product this row belongs to, decided here and persisted on the sale (issue
            // #758). The rule is the one this import has always applied - the recorded provider id
            // first (issue #745), then the unambiguous name - but its answer is now stored instead
            // of re-derived by every later reader.
            var facts = new ImportedNayaxSale(
                row.TransactionId,
                row.MachineId,
                instant.Value,
                row.TransactionStatusId,
                ProductMatcher.MatchLinked(
                    candidates, LocalProductId(row.NayaxProductId, localProductIdByNayaxId), row.ProductName),
                row.NayaxProductId,
                row.MachineName,
                row.SettlementValue,
                row.PaymentMethod,
                row.ProductName,
                row.NayaxProductCostPrice);

            CostableSale sale;
            if (stored is null)
            {
                sale = _store.Add(facts);
                imported++;
            }
            else
            {
                // The stored row's own product and instant are tracked before it is overwritten, so
                // an update that moves a completed sale to another product or an earlier time
                // replays both the product it left and the product it joined.
                TrackStoredProduct(stored, affected);
                sale = _store.Update(
                    stored,
                    facts with
                    {
                        // Never erase a transaction cost price the business already holds with a
                        // row that simply does not carry the column.
                        NayaxProductCostPrice = facts.NayaxProductCostPrice ?? stored.NayaxProductCostPrice,

                        // A conflicting sale keeps the product it has - none - until a human names
                        // one through the audited operation (issue #758).
                        ProductId = conflictingTransactionIds.Contains(row.TransactionId)
                            ? stored.ProductId
                            : facts.ProductId,
                    });
                updated++;
            }

            var cost = await _saleCosting.Handle(sale, cancellationToken: cancellationToken);
            if (cost is not null)
                _store.StageCost(sale, cost);
            TrackImportedProduct(sale, affected);
        }

        if (imported > 0 || updated > 0)
        {
            await _store.SaveChangesAsync(cancellationToken);
            await RebuildPostTransitionProductsAsync(affected, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }

        return new NayaxSalesImportResult(imported, updated, skipped);
    }

    /// <summary>
    /// The instant a row is imported at (issue #380), or <c>null</c> when it has no trustworthy one
    /// and must be skipped:
    /// <list type="number">
    ///   <item>A usable <c>AuthorizationDateTimeGMT</c> value is the authoritative instant, for a new
    ///   sale and for a stored one alike, so it may correct an older stored instant through the
    ///   ordinary update and cost-rebuild path.</item>
    ///   <item>Otherwise a stored sale keeps the instant it already holds. A later export without a
    ///   usable GMT value must not move a sale the latest-sales synchronization stored at its
    ///   authoritative instant, and that synchronization never rewrites a stored instant, so nothing
    ///   would ever move it back. The row's other facts still update the sale.</item>
    ///   <item>A new sale whose GMT value is present but unreadable is skipped: the export claimed an
    ///   authoritative instant and it could not be read.</item>
    ///   <item>A new sale from an export without the GMT column, or with a blank GMT cell, is imported
    ///   at the export's own <c>MachineAuthorizationTime</c> column, read exactly as earlier imports
    ///   read it. Nayax publishes no timezone contract for that column, so the value is not converted
    ///   with an invented timezone and is not claimed to be a verified UTC instant; whether such rows
    ///   should be refused instead is an open owner decision recorded in docs/architecture.md.</item>
    /// </list>
    /// </summary>
    private static DateTime? ResolveInstant(NayaxSalesImportRow row, CostableSale? stored)
    {
        if (row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.Valid && row.AuthorizationDateTimeGmt is { } gmt)
            return gmt;
        if (stored is not null)
            return stored.AuthorizationTime;
        if (row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.Malformed)
            return null;
        return row.MachineAuthorizationTime;
    }

    /// <summary>
    /// Records the product the row just imported affected: the association the import resolved and
    /// persisted onto the sale (issue #758). An unresolved row affected no product's inventory and
    /// stays visible through its own row and its costing provenance instead.
    /// </summary>
    private static void TrackImportedProduct(CostableSale sale, IDictionary<long, DateTime> affected)
    {
        if (!NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            return;

        RecordAffectedProduct(sale.ProductId, sale, affected);
    }

    /// <summary>
    /// Records the product a <em>stored</em> sale affected before this import overwrites it, read
    /// from the association persisted on that row (issue #758) rather than re-derived from the
    /// provider's product id or the reported name. That is what makes a re-import that moves a
    /// transaction to another product replay both the product it left and the product it joined,
    /// in one pass, from facts the previous writer decided.
    /// </summary>
    private static void TrackStoredProduct(CostableSale stored, IDictionary<long, DateTime> affected)
    {
        if (!NayaxTransactionStatusClassifier.IsCompletedSale(stored.TransactionStatusId))
            return;

        RecordAffectedProduct(stored.ProductId, stored, affected);
    }

    /// <summary>
    /// Records the product a completed sale affected, keeping the earliest such sale's instant.
    /// A sale that is not a completed sale changed no costed inventory, and an unmatched product
    /// has no inventory to replay - both stay visible through the sale's own status and costing
    /// provenance instead.
    /// </summary>
    private static void RecordAffectedProduct(
        long? productId,
        CostableSale sale,
        IDictionary<long, DateTime> affected)
    {
        if (productId is not null &&
            (!affected.TryGetValue(productId.Value, out var existing) || sale.AuthorizationTime < existing))
            affected[productId.Value] = sale.AuthorizationTime;
    }

    /// <summary>
    /// The local product an imported row's <c>NayaxProductID</c> names, or <see langword="null"/>
    /// when this business has recorded that Nayax id on no product.
    /// </summary>
    private static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;

    /// <summary>
    /// Replays the inventory cost of every affected product whose transition baseline cutoff the
    /// earliest affecting sale actually falls after. A sale at or before a product's cutoff is
    /// covered by that baseline, so replaying it would recost history the transition owns.
    /// </summary>
    private async Task RebuildPostTransitionProductsAsync(
        IReadOnlyDictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        if (affected.Count == 0)
            return;

        var cutoffs = await _store.GetTransitionCutoffsAsync(affected.Keys.ToList(), cancellationToken);
        foreach (var item in affected)
            if (cutoffs.TryGetValue(item.Key, out var cutoff) && item.Value > cutoff)
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: cancellationToken);
    }
}
