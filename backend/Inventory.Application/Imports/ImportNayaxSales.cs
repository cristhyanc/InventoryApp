using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Imports;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>
/// One row of an uploaded Nayax sales file that was not applied because a value it does carry
/// cannot be read (issue #1298) - an amount written as <c>$3.50</c>, a status that is not a number.
/// It names the row and the transaction so an operator can find and fix it in the file; nothing of
/// the row reaches a stored sale, and nothing of it is logged.
/// </summary>
/// <param name="RowNumber">The row's own number in the file, counting the header row as row 1.</param>
/// <param name="TransactionId">The remote transaction identifier the row names.</param>
public sealed record NayaxSalesImportInvalidRow(int RowNumber, long TransactionId);

/// <summary>
/// Counts of one uploaded Nayax sales import; the <c>POST api/imports/nayax-sales</c> response
/// body, moved here from <c>InventoryApi.Services.Interfaces</c> with no JSON change (issue #301).
/// A row the import could not identify is counted as <paramref name="Skipped"/> and stays visible
/// rather than being silently dropped.
///
/// <para>Issue #1298 added the two facts an operator could not otherwise see: how many rows the
/// file said nothing new about, and which rows carried a value nobody could read. The three
/// original counts are unchanged, so the response only gained keys.</para>
/// </summary>
/// <param name="Imported">Rows stored as a new sale.</param>
/// <param name="Updated">Rows that updated a sale this business already held.</param>
/// <param name="Skipped">Rows with no usable identity, instant, or - for a new sale - amount.</param>
/// <param name="KeptStoredValues">
/// Updated rows that kept a stored value because the file's cell was blank or its column was
/// missing - the status, the amount, the payment method, the product name or the machine name -
/// counted once per row however many of its fields were kept. See
/// <c>ImportNayaxSales.KeptAStoredValue</c> for when a kept value is one.
/// </param>
public sealed record NayaxSalesImportResult(int Imported, int Updated, int Skipped, int KeptStoredValues = 0)
{
    /// <summary>
    /// The rows that carried a non-blank status or amount this import could not read, in file
    /// order. Each one changed nothing.
    /// </summary>
    public IReadOnlyList<NayaxSalesImportInvalidRow> InvalidRows { get; init; } = [];

    /// <summary>How many rows were invalid; the count the Imports page shows beside the others.</summary>
    public int Invalid => InvalidRows.Count;

    /// <summary>
    /// Value equality over the counts and the invalid rows <em>in order</em>. The generated one
    /// would compare the list by reference, which would make two results reporting the same rows
    /// unequal.
    /// </summary>
    public bool Equals(NayaxSalesImportResult? other) =>
        other is not null
        && Imported == other.Imported
        && Updated == other.Updated
        && Skipped == other.Skipped
        && KeptStoredValues == other.KeptStoredValues
        && InvalidRows.SequenceEqual(other.InvalidRows);

    public override int GetHashCode() =>
        HashCode.Combine(Imported, Updated, Skipped, KeptStoredValues, InvalidRows.Count);
}

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
///   <item>The sale instant follows the precedence <see cref="NayaxSalesRowApplier"/> documents
///   (issue #380): a usable <c>AuthorizationDateTimeGMT</c> value is authoritative and may correct a
///   stored instant; without one, a stored sale keeps the instant it already has, and only a new
///   sale falls back to the export's own <c>MachineAuthorizationTime</c> column, whose timezone is
///   unverified because Nayax publishes no contract for the export. A GMT value the export carries
///   but that cannot be read never triggers that fallback.</item>
///   <item>A transaction this business already holds is updated in place rather than double
///   counted; a transaction another business holds is a new sale of this one, because a remote
///   <c>TransactionID</c> is unique only within the operator account that issued it. One
///   <em>file</em> naming a transaction twice is the same rule: its later row updates the sale its
///   earlier row inserted or updated, so the file's last word about a transaction is what the
///   business ends up holding and the sale exists once (issue #1300).</item>
///   <item><b>What this business already holds is read once, for the whole file, inside the
///   import's own write-locked transaction</b> (issue #1300). It used to be one query per row,
///   outside any transaction: a 4,000-row upload took 30 seconds and an 8,000-row one 124, and a
///   transaction the Nayax SQS queue reader stored between a row's lookup and the save failed the
///   whole upload on the unique index on <c>(BusinessId, TransactionID)</c> with a <c>500</c>. Now
///   the lookup and the save are one serialised operation, so such a row is found and updated. The
///   cost replay deliberately stays outside the lock - see
///   <see cref="INayaxSalesImportTransactions"/> for both halves of that.</item>
///   <item><b>A file beyond the import's limits is refused before anything is read or written</b>
///   (issue #1300, <see cref="NayaxSalesFilePolicy"/>): more data rows than
///   <see cref="NayaxSalesFilePolicy.MaxRows"/>, or an XLSX whose sheet data expands past
///   <see cref="NayaxSalesFilePolicy.MaxUncompressedSheetBytes"/>. The refusal is the same
///   <see cref="InvalidOperationException"/> the unsupported-format rule raises, so the operator
///   gets the same <c>400 Bad Request</c> with the sentence
///   <see cref="NayaxSalesFilePolicy.TooLargeMessage"/> states, and nothing is stored.</item>
///   <item>An update never erases a historical cost the business already has: a row carrying no
///   transaction cost price leaves the stored one in place, and the cost actually applied to the
///   sale stays <see cref="ICostSale"/>'s decision with its recorded provenance.</item>
///   <item><b>Nor does it erase any other stored fact with a cell the file left empty</b> (issue
///   #1298). The file is the source of truth for a transaction's status and amount: a value it
///   carries replaces the stored one in either direction, an explicit <c>0</c> amount included.
///   But a blank cell and a missing column say nothing about a transaction - and for the three text
///   facts a cell holding nothing but whitespace is a blank cell - so the stored status,
///   amount, payment method, product name and machine name are kept, and the row is counted once in
///   <see cref="NayaxSalesImportResult.KeptStoredValues"/>. A non-blank value that cannot be read
///   is never mistaken for a blank one: the row is applied nowhere and is reported in
///   <see cref="NayaxSalesImportResult.InvalidRows"/> with its row number. A <em>new</em> sale
///   whose amount is blank is skipped rather than stored as a $0 sale.</item>
///   <item><b>The per-row rules themselves live in <see cref="NayaxSalesRowApplier"/></b> (issue
///   #1304) and are unchanged by that move. The sales history import of that issue applies the same
///   rules to each of its batches, so "the history import applies the same row rules as the normal
///   import" is true by construction rather than by two copies agreeing. What stays here is what is
///   this import's own: the format and limit refusal, the one read of the whole file, the single
///   write-locked transaction it is all applied in, and the cost replay afterwards.</item>
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
    /// <summary>
    /// What this import is willing to read, from the Domain policy that owns both numbers
    /// (issue #1300). Stated once here rather than at each call, so the reader is never asked to
    /// read an uploaded sales file under some other ceiling.
    /// </summary>
    private static readonly NayaxSalesFileLimits Limits = new(
        NayaxSalesFilePolicy.MaxRows, NayaxSalesFilePolicy.MaxUncompressedSheetBytes);

    private readonly INayaxSalesWorkbookReader _workbook;
    private readonly INayaxSalesImportStore _store;
    private readonly INayaxSalesImportTransactions _transactions;
    private readonly NayaxSalesRowApplier _rows;
    private readonly IRebuildProductCost _inventoryCostRebuild;
    private readonly INayaxProductLinkStore _productLinks;

    public ImportNayaxSales(
        INayaxSalesWorkbookReader workbook,
        INayaxSalesImportStore store,
        INayaxSalesImportTransactions transactions,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild,
        INayaxProductLinkStore productLinks)
    {
        _workbook = workbook;
        _store = store;
        _transactions = transactions;

        // The per-row rules, over the same store and the same costing this import was given
        // (issue #1304). It is constructed rather than injected because it is not a port and holds
        // no state of its own: it is the rules themselves, shared with the sales history import so
        // the two cannot drift apart, and this import's own constructor stays exactly what it was.
        _rows = new NayaxSalesRowApplier(store, saleCosting);
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

        NayaxSalesWorkbookReadResult read;
        using (var content = file.OpenReadStream())
        {
            read = _workbook.Read(content, file.FileName, Limits);
        }

        // Beyond the limits the Domain policy fixes (issue #1300). The reader stopped instead of
        // reading the file, so nothing was parsed, nothing is looked up and nothing is written: the
        // operator gets the sentence that says what to do with the file instead of a server that
        // spends a gigabyte on it and then holds the database for minutes.
        if (read.ExceedsLimits)
            throw new InvalidOperationException(NayaxSalesFilePolicy.TooLargeMessage);

        var rows = read.Rows;

        // An export with no data rows reads nothing and writes nothing - not even the catalogue.
        if (rows.Count == 0)
            return new NayaxSalesImportResult(0, 0, 0);

        // What the rows are merged against, read once for the whole file: the catalogue; the sales
        // whose two candidate products disagreed when issue #758 resolved the existing history, so
        // that a re-import updates every other fact of such a sale and leaves its product exactly as
        // it is (expected to be empty); and the export's Nayax product ids resolved to local product
        // ids through the one lookup over the recorded provider ids (issue #745, parent #329 decision
        // A2b), instead of being compared with Product.Id - which only ever worked because the local
        // key *is* the Nayax id. Awaited in sequence with the per-row reads below: they all land on
        // the request's own scoped context (docs/architecture.md § Concurrency inside one request).
        var facts = new NayaxSalesRowFacts(
            await _store.GetProductCandidatesAsync(cancellationToken),
            (await _store.GetConflictingTransactionIdsAsync(cancellationToken)).ToHashSet(),
            await _productLinks.GetLocalProductIdsAsync(
                NayaxSalesRowApplier.NayaxProductIdsOf(rows), cancellationToken));

        // Each affected product mapped to the earliest completed sale that touched it, which is the
        // instant its cost replay has to restart from.
        var affected = new Dictionary<long, DateTime>();

        // The find-and-save, inside one write-locked transaction (issue #1300): the stored sales of
        // every transaction the file names are read *there*, each row is merged, costed and staged
        // against what that read found, and the sales are saved and committed before the transaction
        // is released. What one row does is NayaxSalesRowApplier's, which is also what the sales
        // history import of issue #1304 applies to each of its batches - so the two imports cannot
        // drift apart on a row rule.
        NayaxSalesAppliedRows applied;
        await using (var transaction = await _transactions.BeginAsync(cancellationToken))
        {
            applied = await _rows.ApplyAsync(rows, facts, affected, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // Outside the write lock, deliberately (issue #1300): the sales are committed, and replaying
        // the affected products' cost takes as long as their histories - which is not a wait every
        // other writer in the application may be made to serve. The order and the failure behaviour
        // are otherwise exactly what they were: replay, then save the replayed costs, so a fatal
        // data-quality failure propagates with the sales saved and the rebuilt costs unsaved.
        if (applied.Imported > 0 || applied.Updated > 0)
        {
            await RebuildPostTransitionProductsAsync(affected, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }

        return new NayaxSalesImportResult(
            applied.Imported, applied.Updated, applied.Skipped, applied.KeptStoredValues)
        {
            InvalidRows = applied.InvalidRows,
        };
    }

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
