using Inventory.Application.Costing;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>
/// What the rows of an uploaded Nayax sales export are merged against: the facts that are read once
/// for a whole file or a whole batch rather than per row (issues #301, #745, #758 and #1298).
/// </summary>
/// <param name="Candidates">The business's product catalogue, as the Domain matcher consumes it.</param>
/// <param name="ConflictingTransactionIds">
/// The transactions issue #758's migration marked conflicting. Such a sale keeps the product it has
/// until a human names one through the audited operation, while every other fact still updates.
/// </param>
/// <param name="LocalProductIdByNayaxId">
/// The local product each remote <c>NayaxProductID</c> in these rows names, resolved through the
/// provider id the business recorded on that product. An id recorded on no product is absent, which
/// is an unknown product and never the product whose local key happens to be that number.
/// </param>
public sealed record NayaxSalesRowFacts(
    IReadOnlyList<ProductMatchCandidate> Candidates,
    IReadOnlySet<long> ConflictingTransactionIds,
    IReadOnlyDictionary<long, long> LocalProductIdByNayaxId);

/// <summary>
/// What one pass over a set of uploaded rows did (issues #1298 and #1300). The counts are the ones
/// the uploaded import has always reported; a batched import adds them up over its batches.
/// </summary>
/// <param name="Imported">Rows stored as a new sale.</param>
/// <param name="Updated">Rows that updated a sale the business already held.</param>
/// <param name="Skipped">Rows with no usable identity, instant, or - for a new sale - amount.</param>
/// <param name="KeptStoredValues">Updated rows that kept at least one stored value, counted once per row.</param>
/// <param name="InvalidRows">Rows carrying a non-blank value that could not be read. Each changed nothing.</param>
public sealed record NayaxSalesAppliedRows(
    int Imported,
    int Updated,
    int Skipped,
    int KeptStoredValues,
    IReadOnlyList<NayaxSalesImportInvalidRow> InvalidRows);

/// <summary>
/// The per-row rules of the uploaded Nayax sales import, in one place so that both imports apply
/// exactly the same ones (issue #1304).
///
/// <para><strong>Nothing here is new.</strong> Every rule in this class was <see cref="ImportNayaxSales"/>'s
/// and is unchanged - the identity facts a row must carry, the unreadable-cell refusal, the instant
/// precedence of issue #380, the blank-cell keep rules of issue #1298, the product resolution of
/// issues #745 and #758, the conflicting-sale exception, the costing through <see cref="ICostSale"/>,
/// the sale carried through to a later row naming the same transaction (issue #1300) and the record
/// of which products a stock-consuming sale affected. It is a class of its own because
/// <see cref="ProcessNayaxSalesHistoryImportJob"/> applies the same rules to a batch of a long
/// history, and "the history import applies the same row rules as the normal import" has to be true
/// by construction rather than by two copies agreeing.</para>
///
/// <para><strong>It is a pass over rows, not a transaction.</strong> The caller opens the
/// write-locked transaction, because what that lock is held across is the caller's decision: the
/// normal upload holds it for one file (bounded by <c>NayaxSalesFilePolicy.MaxRows</c>) and the
/// history job holds it for one batch (<c>NayaxSalesHistoryPolicy.BatchRows</c>). What this class
/// guarantees is the part that must happen inside it: the stored sales of exactly the rows that could
/// be applied are read <em>here</em>, so a transaction the Nayax SQS queue reader stored while the
/// file was being read is found and updated instead of being inserted a second time and refused by
/// the unique index on <c>(BusinessId, TransactionID)</c>.</para>
/// </summary>
public sealed class NayaxSalesRowApplier
{
    private readonly INayaxSalesImportStore _store;
    private readonly ICostSale _saleCosting;

    public NayaxSalesRowApplier(INayaxSalesImportStore store, ICostSale saleCosting)
    {
        _store = store;
        _saleCosting = saleCosting;
    }

    /// <summary>
    /// The distinct remote product ids these rows name, which is what a caller resolves through
    /// <c>INayaxProductLinkStore</c> before it opens its transaction. Stated once here so both
    /// imports ask the same question of the same rows.
    /// </summary>
    public static IReadOnlyList<long> NayaxProductIdsOf(IEnumerable<NayaxSalesImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .Where(row => row.NayaxProductId.HasValue)
            .Select(row => row.NayaxProductId!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Applies <paramref name="rows"/> in file order and saves the sales they produced. The caller
    /// must already hold the write-locked transaction this runs in, and commits it.
    ///
    /// <para><strong>What one read for the whole pass may not become is one answer for the whole
    /// pass.</strong> A file may name the same transaction more than once, and the row that comes
    /// second has to see what the row before it applied - not what the lookup found before either of
    /// them ran. So the state each transaction is in is carried through the pass and refreshed from
    /// every row that applied one (issue #1300). Without that, a second row whose cells are blank
    /// restores the values the first row had just overwritten, and a second row naming a transaction
    /// the file itself introduced inserts it twice.</para>
    /// </summary>
    /// <param name="rows">The rows to apply, in file order.</param>
    /// <param name="facts">What they are merged against, read once by the caller.</param>
    /// <param name="affected">
    /// Each product a stock-consuming sale touched, mapped to the earliest such sale's instant -
    /// which is the instant its cost replay has to restart from. The caller owns the dictionary: the
    /// normal upload replays the products itself after its commit, and the history job records them
    /// as pending rebuilds in the batch's own transaction.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public async Task<NayaxSalesAppliedRows> ApplyAsync(
        IReadOnlyList<NayaxSalesImportRow> rows,
        NayaxSalesRowFacts facts,
        IDictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(affected);

        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var keptStoredValues = 0;
        var invalidRows = new List<NayaxSalesImportInvalidRow>();

        // One read for the rows that could be applied, inside the caller's lock. It replaces a query
        // per row, and because it is made here rather than before the lock, what it answers is the
        // state this save is actually decided on.
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
                facts,
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

        return new NayaxSalesAppliedRows(imported, updated, skipped, keptStoredValues, invalidRows);
    }

    /// <summary>
    /// What one row whose identity and cell values the pass above accepted does to this business's
    /// sales: the merge with what is stored, the insert or the update, the costing and the record of
    /// which products to replay.
    ///
    /// <para>The row reaching here is already known identifiable and free of unreadable values, and
    /// <paramref name="stored"/> is the sale held under the row's transaction identifier as this
    /// import last saw it: what the read inside the transaction found, or what an earlier row of the
    /// same pass applied to it - <see langword="null"/> for a transaction neither the business nor
    /// the rows before this one hold.</para>
    /// </summary>
    private async Task<AppliedRow> ApplyRowAsync(
        NayaxSalesImportRow row,
        StoredImportedSale? stored,
        NayaxSalesRowFacts facts,
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
        var merged = new ImportedNayaxSale(
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
                facts.Candidates,
                LocalProductId(row.NayaxProductId, facts.LocalProductIdByNayaxId),
                productName),
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
            sale = _store.Add(merged);
            applied = AppliedRow.NewSale(SaleLeftBehind(sale, merged));
        }
        else
        {
            // The stored row's own product and instant are tracked before it is overwritten, so
            // an update that moves a completed sale to another product or an earlier time
            // replays both the product it left and the product it joined.
            TrackStoredProduct(stored.Sale, affected);
            var written = merged with
            {
                // A conflicting sale keeps the product it has - none - until a human names
                // one through the audited operation (issue #758).
                ProductId = facts.ConflictingTransactionIds.Contains(row.TransactionId)
                    ? stored.Sale.ProductId
                    : merged.ProductId,
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
    /// above, which skips such a row, and the stored-sale read above it, which must ask about exactly
    /// the rows that could be applied.</para>
    /// </summary>
    private static bool IsIdentifiable(NayaxSalesImportRow row) =>
        row.TransactionId > 0
        && row.MachineId > 0
        && (row.MachineAuthorizationTime is not null
            || row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.Valid);

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
    /// The sale the row left behind, for a later row of the same pass naming the same transaction,
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
        /// <see cref="NayaxSalesAppliedRows.KeptStoredValues"/> when it kept a stored value.
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
}
