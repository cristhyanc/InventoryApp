using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Imports;
using Inventory.Domain.NayaxSqs;
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
///   <item>The sale instant follows the precedence <see cref="ResolveInstant"/> documents
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
    private readonly ICostSale _saleCosting;
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

        // Each affected product mapped to the earliest completed sale that touched it, which is the
        // instant its cost replay has to restart from.
        var affected = new Dictionary<long, DateTime>();

        var applied = await ApplyRowsAsync(
            rows, candidates, conflictingTransactionIds, localProductIdByNayaxId, affected, cancellationToken);

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
    /// The find-and-save, inside one write-locked transaction (issue #1300): the stored sales of
    /// every transaction the file names are read <em>there</em>, each row is merged, costed and
    /// staged against what that read found, and the sales are saved and committed before the
    /// transaction is released.
    ///
    /// <para>What that buys is the race the Nayax SQS queue reader introduced (issue #1103). Both
    /// paths read "is this transaction stored?" and then insert through the same unique index on
    /// <c>(BusinessId, TransactionID)</c>; with the read outside a transaction, a row the reader
    /// stored in between failed this whole upload with a <c>500</c>. Inside it, the reader's row is
    /// either already visible to the read here - and is updated, which is what the file asked for -
    /// or waits for this commit and enriches the row this import inserted.</para>
    ///
    /// <para>Nothing else moved: the per-row rules, their order and the counts are unchanged, and a
    /// file that produced no insert and no update still saves nothing at all.</para>
    ///
    /// <para><strong>What one read for the whole file may not become is one answer for the whole
    /// file.</strong> A file may name the same transaction more than once, and the row that comes
    /// second has to see what the row before it applied - not what the lookup found before either of
    /// them ran. So the state each transaction is in is carried through the pass and refreshed from
    /// every row that applied one, which is how the per-row lookup this replaced behaved: it read the
    /// row again, already updated. Without that, a second row whose cells are blank restores the
    /// values the first row had just overwritten (issue #1298's keep rule, applied against a stale
    /// sale), and a second row naming a transaction the <em>file</em> introduced inserts it a second
    /// time - which the unique index refuses, or, where the index cannot see it, double counts.</para>
    /// </summary>
    private async Task<AppliedRows> ApplyRowsAsync(
        IReadOnlyList<NayaxSalesImportRow> rows,
        IReadOnlyList<ProductMatchCandidate> candidates,
        IReadOnlySet<long> conflictingTransactionIds,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        IDictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var keptStoredValues = 0;
        var invalidRows = new List<NayaxSalesImportInvalidRow>();

        await using var transaction = await _transactions.BeginAsync(cancellationToken);

        // One read for the whole file, inside the lock (issue #1300). It replaces a query per row -
        // 20,000 of them for a file at the row limit - and, because it is made here rather than
        // before the lock, what it answers is the state this save is actually decided on. Only the
        // rows that could be applied are asked about, through the same rule the loop skips on, so
        // the read is the file's own transactions and not every number in it.
        //
        // It is the pass's starting state and not its only one: every row that applies a sale writes
        // that sale back here, so a second row naming the same transaction merges against what the
        // first row left rather than against what the business held before the file ran.
        var storedByTransactionId = new Dictionary<long, StoredImportedSale>(
            await _store.FindByTransactionIdsAsync(
                rows
                    .Where(IsIdentifiable)
                    .Select(row => row.TransactionId)
                    .Distinct()
                    .ToList(),
                cancellationToken));

        foreach (var row in rows)
        {
            if (!IsIdentifiable(row))
            {
                skipped++;
                continue;
            }

            // A value the row does carry and this import cannot read is never read as "the file
            // says nothing" (issue #1298): nothing of such a row is applied, and it is reported
            // with its row number so the operator can fix the file rather than discover later that
            // a sale quietly changed.
            if (row.TransactionStatusInput == NayaxSalesCellInput.Malformed ||
                row.SettlementValueInput == NayaxSalesCellInput.Malformed)
            {
                invalidRows.Add(new NayaxSalesImportInvalidRow(row.RowNumber, row.TransactionId));
                continue;
            }

            var applied = await ApplyRowAsync(
                row,
                storedByTransactionId.GetValueOrDefault(row.TransactionId),
                candidates,
                conflictingTransactionIds,
                localProductIdByNayaxId,
                affected,
                cancellationToken);

            imported += applied.Imported;
            updated += applied.Updated;
            skipped += applied.Skipped;
            keptStoredValues += applied.KeptStoredValues;

            // The sale this row left behind, for a later row naming the same transaction. A row that
            // applied nothing left the transaction exactly as it was.
            if (applied.Stored is not null)
                storedByTransactionId[row.TransactionId] = applied.Stored;
        }

        if (imported > 0 || updated > 0)
            await _store.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AppliedRows(imported, updated, skipped, keptStoredValues, invalidRows);
    }

    /// <summary>
    /// What one row whose identity and cell values the pass above accepted does to this business's
    /// sales: the merge with what is stored, the insert or the update, the costing and the record of
    /// which products to replay. It runs inside the write-locked transaction that pass opened, and
    /// reports what it did as the counts it adds, so the pass stays a pass over rows.
    ///
    /// <para>The row reaching here is already known identifiable and free of unreadable values, and
    /// <paramref name="stored"/> is the sale held under the row's transaction identifier as this
    /// import last saw it: what the read inside the transaction found, or what an earlier row of the
    /// same file applied to it - <see langword="null"/> for a transaction neither the business nor
    /// the rows before this one hold. The sale this row leaves behind comes back as
    /// <see cref="AppliedRow.Stored"/>, which is what makes the second occurrence of a transaction
    /// merge against the first one's values.</para>
    ///
    /// <para>The rules here are unchanged and so is their order; they are a method of their own only
    /// because one method holding both the pass and every per-row rule was past the complexity this
    /// repository allows.</para>
    /// </summary>
    private async Task<AppliedRow> ApplyRowAsync(
        NayaxSalesImportRow row,
        StoredImportedSale? stored,
        IReadOnlyList<ProductMatchCandidate> candidates,
        IReadOnlySet<long> conflictingTransactionIds,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        IDictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        var instant = ResolveInstant(row, stored?.Sale);
        if (instant is null)
            return AppliedRow.SkippedRow;

        // A new sale has no stored amount to keep, and a blank cell is not a zero sale: such a
        // row is skipped and stays visible, exactly as a row with no usable identity does.
        if (stored is null && row.SettlementValue is null)
            return AppliedRow.SkippedRow;

        // What this row actually states, with the stored value kept wherever it states nothing
        // (issue #1298). The file is still the source of truth for every value it carries - a
        // present status replaces the stored one in either direction, and an explicit 0 amount
        // is a value it carries - so only a blank cell or a missing column is ignored. For the
        // three text facts, a cell holding nothing but whitespace is a blank cell: StatedText
        // decides that here, where the keep-or-overwrite rule lives, rather than relying on how
        // a reader happened to trim it.
        var productName = StatedText(row.ProductName) ?? stored?.Sale.ProductName;
        var facts = new ImportedNayaxSale(
            row.TransactionId,
            row.MachineId,
            instant.Value,
            row.TransactionStatusId ?? stored?.Sale.TransactionStatusId,
            // The local product this row belongs to, decided here and persisted on the sale
            // (issue #758). The rule is the one this import has always applied - the recorded
            // provider id first (issue #745), then the unambiguous name - but its answer is now
            // stored instead of re-derived by every later reader. It is resolved against the
            // name that will be stored, so a kept product name keeps the association it names.
            ProductMatcher.MatchLinked(
                candidates, LocalProductId(row.NayaxProductId, localProductIdByNayaxId), productName),
            row.NayaxProductId,
            StatedText(row.MachineName) ?? stored?.MachineName,
            row.SettlementValue ?? stored!.SettlementValue,
            StatedText(row.PaymentMethod) ?? stored?.PaymentMethod,
            productName,
            // Never erase a transaction cost price the business already holds with a row that
            // simply does not carry the column.
            row.NayaxProductCostPrice ?? stored?.Sale.NayaxProductCostPrice);

        CostableSale sale;
        AppliedRow applied;
        if (stored is null)
        {
            sale = _store.Add(facts);
            applied = AppliedRow.NewSale(SaleLeftBehind(sale, facts));
        }
        else
        {
            // The stored row's own product and instant are tracked before it is overwritten, so
            // an update that moves a completed sale to another product or an earlier time
            // replays both the product it left and the product it joined.
            TrackStoredProduct(stored.Sale, affected);
            var written = facts with
            {
                // A conflicting sale keeps the product it has - none - until a human names
                // one through the audited operation (issue #758).
                ProductId = conflictingTransactionIds.Contains(row.TransactionId)
                    ? stored.Sale.ProductId
                    : facts.ProductId,
            };
            sale = _store.Update(stored.Sale, written);
            applied = AppliedRow.UpdatedSale(KeptAStoredValue(row, stored), SaleLeftBehind(sale, written));
        }

        var cost = await _saleCosting.Handle(sale, cancellationToken: cancellationToken);
        if (cost is not null)
            _store.StageCost(sale, cost);
        TrackImportedProduct(sale, affected);

        return applied;
    }

    /// <summary>
    /// The sale a row has just inserted or updated, in the shape the next row naming the same
    /// transaction has to read it in (issue #1300). <paramref name="sale"/> is the view the store
    /// answered for the row it wrote, so it carries what was actually written - including the
    /// product a conflicting sale kept and the queue verification the stored row holds - and
    /// <paramref name="written"/> supplies the three imported facts the costing view does not carry
    /// and a blank cell must not erase (issue #1298).
    ///
    /// <para>Its costing columns are the ones from before this row's decided cost was staged, which
    /// nothing in the merge reads: the next row costs the fresh view
    /// <see cref="INayaxSalesImportStore.Update"/> answers for it, read from the row itself.</para>
    /// </summary>
    private static StoredImportedSale SaleLeftBehind(CostableSale sale, ImportedNayaxSale written) =>
        new(sale, written.SettlementValue, written.PaymentMethod, written.MachineName);

    /// <summary>
    /// Whether a row carries the three identity facts the import refuses to guess at: a positive
    /// transaction identifier, a positive machine identifier and an authorization time. A row
    /// without them is skipped and stays visible, never imported with a defaulted identity.
    ///
    /// <para>It is one rule in one place because two things ask the question (issue #1300): the loop
    /// below, which skips such a row, and the stored-sale read above, which must ask about exactly
    /// the rows that could be applied.</para>
    /// </summary>
    private static bool IsIdentifiable(NayaxSalesImportRow row) =>
        row.TransactionId > 0
        && row.MachineId > 0
        && (row.MachineAuthorizationTime is not null
            || row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.Valid);

    /// <summary>
    /// What the write-locked pass over the file's rows did, as the result's counts (issue #1300).
    /// The counts themselves are unchanged in meaning; this type exists only because the pass is a
    /// method now rather than the body of <see cref="Handle"/>.
    /// </summary>
    private sealed record AppliedRows(
        int Imported,
        int Updated,
        int Skipped,
        int KeptStoredValues,
        IReadOnlyList<NayaxSalesImportInvalidRow> InvalidRows);

    /// <summary>
    /// What <em>one</em> row did, as the counts it adds to those of the whole pass. Exactly one of
    /// the three outcomes a row can have is named by the three values below, so the pass adds them
    /// without re-deciding anything the row's own rules already decided.
    /// </summary>
    /// <param name="Imported">1 for a row stored as a new sale, otherwise 0.</param>
    /// <param name="Updated">1 for a row that updated a sale already held, otherwise 0.</param>
    /// <param name="Skipped">1 for a row applied nowhere, otherwise 0.</param>
    /// <param name="KeptStoredValues">1 for an updated row that kept a stored value, otherwise 0.</param>
    /// <param name="Stored">
    /// The sale the row left behind, for a later row of the same file naming the same transaction,
    /// or <see langword="null"/> when the row applied nothing and so left the transaction exactly as
    /// it was (issue #1300).
    /// </param>
    private readonly record struct AppliedRow(
        int Imported, int Updated, int Skipped, int KeptStoredValues, StoredImportedSale? Stored)
    {
        /// <summary>A row applied nowhere: no trustworthy instant, or a new sale with no amount.</summary>
        public static AppliedRow SkippedRow { get; } = new(0, 0, 1, 0, null);

        /// <summary>A row stored as a sale neither the business nor an earlier row of the file held.</summary>
        public static AppliedRow NewSale(StoredImportedSale stored) => new(1, 0, 0, 0, stored);

        /// <summary>
        /// A row that updated a sale already held, counted once in
        /// <see cref="NayaxSalesImportResult.KeptStoredValues"/> when it kept a stored value.
        /// </summary>
        public static AppliedRow UpdatedSale(bool keptStoredValue, StoredImportedSale stored) =>
            new(0, 1, 0, keptStoredValue ? 1 : 0, stored);
    }

    /// <summary>
    /// Whether an updated row left a stored value in place because the file carried none for it
    /// (issue #1298): every field the merge above keeps - the status, the amount, the payment
    /// method, the product name and the machine name - so the number an operator reads is the
    /// rows this file said nothing new about, whichever of those facts it said nothing about.
    ///
    /// <para>It is a count of rows, not of fields: a row that kept four of them still counts once,
    /// which is what makes that number one an operator can act on rather than a total of fields.</para>
    ///
    /// <para>A kept payment method, product name or machine name counts only where the stored sale
    /// <em>holds</em> one. An export written without those columns is ordinary and long-standing,
    /// and a column absent from the file for a sale that has nothing stored under it preserves
    /// nothing - counting it would report a kept value where no value exists. The same holds for
    /// the status: the stored status column is nullable and this import can store a sale with no
    /// status, so a blank status counts only where the stored sale holds one. The amount needs no
    /// such check: the stored amount column is never null, so an absent amount always keeps one.</para>
    /// </summary>
    private static bool KeptAStoredValue(NayaxSalesImportRow row, StoredImportedSale stored) =>
        (row.TransactionStatusId is null && stored.Sale.TransactionStatusId is not null)
        || row.SettlementValue is null
        || KeptStoredText(row.PaymentMethod, stored.PaymentMethod)
        || KeptStoredText(row.ProductName, stored.Sale.ProductName)
        || KeptStoredText(row.MachineName, stored.MachineName);

    /// <summary>
    /// Whether the merge kept one stored text fact: the file stated none - exactly the condition
    /// <see cref="StatedText"/> makes the merge keep the stored value on - and the stored sale holds
    /// one to keep.
    /// </summary>
    private static bool KeptStoredText(string? fromFile, string? storedValue) =>
        StatedText(fromFile) is null && !string.IsNullOrWhiteSpace(storedValue);

    /// <summary>
    /// What the file states in one text cell, or <see langword="null"/> when it states nothing
    /// (issue #1298): no column, an empty cell, or a cell holding nothing but whitespace. One
    /// notion of "the file said nothing" for the merge and for the kept count, so a row of spaces
    /// cannot erase a stored payment method, product name or machine name - the reading the status
    /// and the amount have had since this change. The value itself is passed through untouched;
    /// trimming stays the reader's business.
    /// </summary>
    private static string? StatedText(string? fromFile) =>
        string.IsNullOrWhiteSpace(fromFile) ? null : fromFile;

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
        // The stock rule, not the completed-sale rule (issue #1107): an imported status-62 row
        // consumes its unit, so the product it belongs to has to be replayed. The import does change
        // a stored row's status when the file carries a valid one (issue #1298) - in either
        // direction, and on a sale the queue path stored - which is why the stored row's own product
        // is tracked before the overwrite and replayed beside the updated one.
        if (!NayaxSaleCountingPolicy.ConsumesStock(sale.TransactionStatusId, sale.SqsVerification))
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
        if (!NayaxSaleCountingPolicy.ConsumesStock(stored.TransactionStatusId, stored.SqsVerification))
            return;

        RecordAffectedProduct(stored.ProductId, stored, affected);
    }

    /// <summary>
    /// Records the product a stock-consuming sale affected, keeping the earliest such sale's instant.
    /// A sale that consumed no unit changed no costed inventory, and an unmatched product has no
    /// inventory to replay - both stay visible through the sale's own status and costing provenance
    /// instead.
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
