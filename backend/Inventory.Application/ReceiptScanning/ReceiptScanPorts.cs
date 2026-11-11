namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// The product and supplier names a receipt is read against (issue #1163) - the one read receipt
/// scanning makes of the business's own data.
///
/// <para><strong>Two columns each, and no business id in either direction.</strong> It is the
/// narrowest port that can answer "which of this business's products might this line be?", and it is
/// deliberately not the product or supplier listing: those carry costs, stock, prices and GST rules,
/// none of which may leave this application. The business is the one the request already resolved,
/// through the ordinary tenant query filters.</para>
/// </summary>
public interface IReceiptScanCatalogueStore
{
    /// <summary>
    /// This business's active products, as ids and names. Inactive products are left out: a line
    /// matched to one would suggest buying stock the business has retired.
    /// </summary>
    Task<IReadOnlyList<ReceiptScanProduct>> ListProductsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// This business's suppliers it still uses, as ids and names.
    /// </summary>
    Task<IReadOnlyList<ReceiptScanSupplier>> ListSuppliersAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One recorded purchase line a missing pack size may be suggested from (issue #1165): what one unit
/// cost, and when that purchase was.
/// </summary>
/// <param name="ProductId">The product the line was for - this business's own product.</param>
/// <param name="UnitCost">
/// The <c>PurchaseItem.UnitCost</c> recorded on that line, which is GST-inclusive like every purchase
/// amount in this application (AGENTS.md § Purchase GST classification).
/// </param>
/// <param name="PurchaseDate">
/// That purchase's own date, as the calendar date the draft publishes beside the suggestion so an
/// operator can see which purchase the number came from.
/// </param>
public sealed record ReceiptScanLastPurchase(long ProductId, decimal UnitCost, DateOnly PurchaseDate);

/// <summary>
/// The most recent recorded purchase line for each of a few products from one supplier (issue #1165,
/// sub-issue E of #975) - the one read the pack-size suggestion makes of the business's own purchase
/// history.
///
/// <para><strong>It is read-only and it is the business's own history.</strong> The method takes no
/// business id in either direction: the business is the one the request already resolved, through the
/// ordinary tenant query filters, so only this business's purchases can be read (AGENTS.md § Tenant
/// ownership and data isolation). Nothing on this port can write, and the scan it serves stores
/// nothing.</para>
///
/// <para><strong>It is deliberately not <c>IProductPurchasePriceHistoryProvider</c></strong> (issue
/// #63's price comparison): that port answers a product's whole cost history across every supplier,
/// for a feature that presents it, while this one answers one figure per product for one supplier.
/// Widening either into the other would make a scan read a history it has no use for.</para>
/// </summary>
public interface IReceiptScanPurchaseHistoryStore
{
    /// <summary>
    /// The latest purchase line for each of <paramref name="productIds"/> whose purchase recorded
    /// <paramref name="supplierId"/> as its supplier - at most one entry per product, and no entry
    /// at all for a product this business has never bought from that supplier.
    ///
    /// <para>"Latest" is <c>Inventory.Domain.Purchases.SupplierPurchaseRecency</c>'s one ordering
    /// rule - the purchase date, then the purchase-item id - which is the same rule
    /// <c>SupplierPriceComparisonPolicy</c> (issue #63) orders its own history with, so two purchases
    /// on one day name the same line here as they do there. An implementation must consume that rule
    /// rather than restate the ordering, and must answer every product in one bounded read rather
    /// than one read per product.</para>
    /// </summary>
    /// <param name="supplierId">The supplier the scan suggested, as this business's own supplier id.</param>
    /// <param name="productIds">The products a pack size might be suggested for.</param>
    /// <param name="cancellationToken">The request's own token.</param>
    Task<IReadOnlyList<ReceiptScanLastPurchase>> ListLatestAsync(
        int supplierId,
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Whether receipt scanning is switched on in this deployment, and the limits it works to (issue
/// #1163). An Application-owned port because the use case needs the answers and must not read
/// configuration: only the composition root does that (AGENTS.md § Architecture rules).
///
/// <para><strong>No key, and nothing key-shaped, is on this port.</strong> It answers whether one is
/// configured, which is all the use case needs to know; the key itself is the adapter's, read once at
/// registration and never published, logged or returned.</para>
/// </summary>
public interface IReceiptScanSettings
{
    /// <summary>
    /// Whether a reader key is configured. <see langword="false"/> is the shipped state: the endpoint
    /// answers "not available" and no outbound call is made.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>The largest image the reader accepts, in bytes.</summary>
    long MaxImageBytes { get; }

    /// <summary>The largest PDF the reader accepts, in bytes.</summary>
    long MaxPdfBytes { get; }

    /// <summary>The most pages the reader accepts in one PDF.</summary>
    int MaxPdfPages { get; }

    /// <summary>How long one read may take before it is abandoned.</summary>
    TimeSpan Timeout { get; }
}

/// <summary>
/// How many pages a PDF has (issue #1163), so a document over the reader's page limit is refused
/// before it is uploaded anywhere.
///
/// <para>A port rather than a Domain function because counting pages means parsing a file format,
/// which is Infrastructure's work (AGENTS.md § Repository map: imported-file readers). The rule about
/// the number it answers - the limit, and what an unreadable PDF means - stays in
/// <c>Inventory.Domain.ReceiptScanning.ReceiptScanFilePolicy</c>.</para>
///
/// <para><strong>The number must be the pages the document can actually reach, never a count it
/// declares about itself.</strong> A page count read from the file's own claim is a limit the file
/// sets for itself: a document declaring one page while holding two hundred would pass the limit and
/// be uploaded whole (the repair of 10 November 2026). The pages counted must be those of the
/// document's <strong>active</strong> object graph, resolved through its own cross-reference
/// information - an incremental update can replace the page tree, and an older definition still in
/// the bytes is history. An implementation that cannot verify the structure or resolve the active
/// graph answers <see langword="null"/>, which is a refusal.</para>
/// </summary>
public interface IPdfPageCounter
{
    /// <summary>
    /// How many pages <paramref name="content"/> holds, verified from the document's own structure,
    /// or <see langword="null"/> when it is not a PDF whose pages this counter can verify - which is
    /// a refusal, never an assumption of one page.
    /// </summary>
    int? Count(ReadOnlySpan<byte> content);
}

/// <summary>
/// The one log line a receipt scan writes (issue #1163).
///
/// <para><strong>Its shape is the privacy rule.</strong> A receipt is a business document and the
/// products on it are the business's own data, so a scan may record that it happened and what it
/// cost - the business, the outcome, how long it took, how many pages and how many tokens - and
/// nothing about what was on the receipt. No parameter here can carry receipt text, a file name, a
/// product name, a supplier name or a draft, which is what makes that structural rather than a matter
/// of care. It is <c>INayaxSqsReaderLog</c>'s rule, for the same reason.</para>
/// </summary>
public interface IReceiptScanLog
{
    /// <summary>Records one finished scan attempt, whatever its outcome.</summary>
    /// <param name="businessId">The business the scan was made in.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="duration">How long the whole attempt took.</param>
    /// <param name="pageCount">The PDF's page count, when the file was a PDF.</param>
    /// <param name="inputTokens">Tokens consumed, when the reader reported them.</param>
    /// <param name="outputTokens">Tokens produced, when the reader reported them.</param>
    void Completed(
        int businessId,
        ScanReceiptOutcome outcome,
        TimeSpan duration,
        int? pageCount,
        int? inputTokens,
        int? outputTokens);
}
