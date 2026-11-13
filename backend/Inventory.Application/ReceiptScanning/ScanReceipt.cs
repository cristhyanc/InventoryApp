using System.Diagnostics;
using Inventory.Application.Purchases;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.ReceiptScanning;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// Reads one receipt into a suggested purchase draft (issue #1163, sub-issue B of #975), inside the
/// business's monthly scan allowance (issue #1164, sub-issue C).
///
/// <para><strong>It stores no receipt, no draft and no purchase.</strong> No file is written, no
/// draft is persisted, no purchase is created and document storage is never reached: the receipt's
/// bytes are read into memory, sent to the reader once, and dropped. The <em>one</em> thing issue
/// #1164 writes is a counter - how many scans this business has made in its own calendar month -
/// which holds no receipt, no file name, no product and no part of a draft. That is the whole of the
/// no-writes criterion as it now stands, and it is still structural: the only writing port here is
/// <see cref="IReceiptScanUsageStore"/>, whose single write increments one integer. Every other
/// persistence port it takes is read-only: the catalogue the receipt is read against, and - since
/// issue #1165 - the last purchase a missing pack size may be suggested from.</para>
///
/// <para><strong>The month's cap is the last thing checked and the first thing written.</strong> A
/// scan is counted when the reader is about to be called - after everything free has been refused,
/// and before the money is spent - so a request refused for its plan, its file, its page count or a
/// switched-off deployment is not counted, and a failure <em>after</em> the call stays counted
/// because the money went. The counter's read, its comparison with the cap and its increment are one
/// serialised operation in the store, so two simultaneous scans cannot both take the last one. The
/// month is the business's own calendar month through <see cref="IBusinessCalendar"/> - never the
/// host's and never UTC's - and the cap itself is
/// <c>Inventory.Domain.Subscriptions.PlanCatalogue.MonthlyReceiptScanLimit</c>.</para>
///
/// <para><strong>The plan is not checked here.</strong> It is the endpoint's own
/// <c>[RequirePlan(PlanFeature.ReceiptScanning)]</c>, which runs before model binding, so a business
/// whose plan does not include scanning never reaches this use case, never has its file read and
/// never costs anything (see <c>InventoryApi.Auth.PlanRequirement</c>). The fail-closed rule that
/// makes that refusal apply while enforcement is off belongs to
/// <c>Inventory.Domain.Subscriptions.PlanFeaturePolicy</c>, one table for every plan decision.</para>
///
/// <para><strong>Nothing is called before everything free has been checked.</strong> The deployment's
/// configuration, then the file rules, then the page count - each refusal happens before the reader
/// is reached, because each of them would otherwise be money spent on a request that cannot
/// succeed.</para>
///
/// <para><strong>One call, no retry</strong> (issue #1163). A timeout and a failure are reported, not
/// repeated: a scan costs money and the operator decides whether to spend it again.</para>
/// </summary>
public sealed class ScanReceipt
{
    private readonly IReceiptScanSettings _settings;
    private readonly IReceiptScanCatalogueStore _catalogue;
    private readonly IReceiptScanPurchaseHistoryStore _purchases;
    private readonly IPdfPageCounter _pages;
    private readonly IReceiptReader _reader;
    private readonly IReceiptScanLog _log;
    private readonly ICurrentBusinessProvider _business;
    private readonly IReceiptScanUsageStore _usage;
    private readonly IBusinessCalendar _calendar;

    public ScanReceipt(
        IReceiptScanSettings settings,
        IReceiptScanCatalogueStore catalogue,
        IReceiptScanPurchaseHistoryStore purchases,
        IPdfPageCounter pages,
        IReceiptReader reader,
        IReceiptScanLog log,
        ICurrentBusinessProvider business,
        IReceiptScanUsageStore usage,
        IBusinessCalendar calendar)
    {
        _settings = settings;
        _catalogue = catalogue;
        _purchases = purchases;
        _pages = pages;
        _reader = reader;
        _log = log;
        _business = business;
        _usage = usage;
        _calendar = calendar;
    }

    /// <summary>
    /// Scans <paramref name="file"/>, or reports why it could not be.
    /// </summary>
    /// <param name="file">
    /// The submitted receipt, as the same transport-free input a purchase document arrives on - it is
    /// the same file, judged by the same purchase document rules plus the reader's own.
    /// </param>
    /// <param name="cancellationToken">The request's own token.</param>
    public async Task<ScanReceiptResult> Handle(
        PurchaseFileInput? file,
        CancellationToken cancellationToken)
    {
        var businessId = await _business.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        int? pageCount = null;
        ReceiptReadResult? read = null;
        ReceiptReaderFailure? failure = null;
        var result = ScanReceiptResult.Refused(ScanReceiptOutcome.Unavailable);

        try
        {
            if (!_settings.IsConfigured)
            {
                // The shipped state. No reader, no outbound call, and nothing read about the
                // business either.
                return result = ScanReceiptResult.Refused(ScanReceiptOutcome.SwitchedOff);
            }

            if (file is null)
            {
                return result = InvalidFile(ReceiptScanFileVerdict.Missing);
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var verdict = ReceiptScanFilePolicy.Check(
                extension, file.Length, _settings.MaxImageBytes, _settings.MaxPdfBytes);
            if (verdict is not ReceiptScanFileVerdict.Accepted)
            {
                return result = InvalidFile(verdict);
            }

            var kind = ReceiptScanFilePolicy.KindOf(extension)!.Value;
            var mediaType = ReceiptScanFilePolicy.MediaTypeOf(extension)!;
            var content = await ReadAsync(file, cancellationToken).ConfigureAwait(false);

            if (kind is ReceiptScanMediaKind.Pdf)
            {
                pageCount = _pages.Count(content);
                var pageVerdict = ReceiptScanFilePolicy.CheckPdfPages(pageCount, _settings.MaxPdfPages);
                if (pageVerdict is not ReceiptScanFileVerdict.Accepted)
                {
                    return result = InvalidFile(pageVerdict);
                }
            }

            var products = await _catalogue.ListProductsAsync(cancellationToken).ConfigureAwait(false);
            var suppliers = await _catalogue.ListSuppliersAsync(cancellationToken).ConfigureAwait(false);

            // The last thing before the reader, and the first thing that writes anything: the
            // month's counter is read, compared with the cap and incremented inside the store's own
            // write-locked transaction, so of two concurrent scans with one left exactly one gets
            // it. A refusal here has called nothing and counted nothing.
            var monthKey = ReceiptScanQuotaPolicy.MonthKeyFor(_calendar.Today);
            if (await _usage
                    .TryCountScanAsync(monthKey, PlanCatalogue.MonthlyReceiptScanLimit, cancellationToken)
                    .ConfigureAwait(false)
                is not { } counted)
            {
                return result = ScanReceiptResult.Refused(
                    ScanReceiptOutcome.LimitReached,
                    ReceiptScanQuotaPolicy.LimitReachedMessage(PlanCatalogue.MonthlyReceiptScanLimit));
            }

            read = await _reader.ReadAsync(
                    new ReceiptReadRequest(
                        new ReceiptScanFile(mediaType, kind, content, pageCount),
                        products,
                        suppliers),
                    cancellationToken)
                .ConfigureAwait(false);

            var payload = ReceiptScanPayloadParser.Parse(read.Json);
            var productIds = products.Select(product => product.Id).ToHashSet();
            var supplierIds = suppliers.Select(supplier => supplier.Id).ToHashSet();
            var history = payload is null
                ? ReceiptPackSizeHistory.None
                : await LastPurchasesAsync(payload, productIds, supplierIds, cancellationToken)
                    .ConfigureAwait(false);

            var draft = payload is null
                ? null
                : ReceiptScanDraftBuilder.Build(payload, productIds, supplierIds, history);

            return result = draft is null
                ? ScanReceiptResult.Refused(ScanReceiptOutcome.Unreadable)
                : ScanReceiptResult.Scanned(
                    draft,
                    new ReceiptScanUsageView(monthKey, counted, PlanCatalogue.MonthlyReceiptScanLimit));
        }
        catch (ReceiptReaderTimeoutException)
        {
            // Exactly one call was made, and it is not made again here.
            return result = ScanReceiptResult.Refused(ScanReceiptOutcome.TimedOut);
        }
        catch (ReceiptReaderUnavailableException exception)
        {
            // What the operator is told is unchanged; why it happened is kept for the log alone
            // (issue #1236), so a production failure can be diagnosed without reproducing the
            // request against the reader.
            failure = exception.Failure;

            return result = ScanReceiptResult.Refused(ScanReceiptOutcome.Unavailable);
        }
        finally
        {
            started.Stop();

            // The business, the outcome, the duration, the pages and the tokens. Never the receipt,
            // the file name, a product name or any part of the draft.
            _log.Completed(
                businessId.Value,
                result.Outcome,
                started.Elapsed,
                pageCount,
                read?.InputTokens,
                read?.OutputTokens);

            if (failure is { } reason)
            {
                // Beside that line, and only for an unavailable outcome the reader said something
                // about: the status, the provider's error type, its capped message and its request
                // id - nothing from the request and nothing from the receipt.
                _log.Unavailable(businessId.Value, reason);
            }
        }
    }

    /// <summary>
    /// The last purchase of each product a pack size might be suggested for (issue #1165), or
    /// <see cref="ReceiptPackSizeHistory.None"/> when there is nothing worth asking for: no supplier
    /// of this business was suggested, or no line is a stock line of a known product with its pack
    /// size missing.
    ///
    /// <para><strong>One statement for the whole receipt, read after the reader has answered.</strong>
    /// It is made only when the answer names something it could apply to, so a refused or unreadable
    /// scan reads no purchase history at all, and it is one call rather than one per line. It is a
    /// read: this use case still has nothing it can write with.</para>
    /// </summary>
    private async Task<ReceiptPackSizeHistory> LastPurchasesAsync(
        ReceiptScanPayload payload,
        IReadOnlySet<long> productIds,
        IReadOnlySet<int> supplierIds,
        CancellationToken cancellationToken)
    {
        if (ReceiptPackSizeLookup.For(payload, productIds, supplierIds) is not { } lookup)
        {
            return ReceiptPackSizeHistory.None;
        }

        var purchases = await _purchases
            .ListLatestAsync(lookup.SupplierId, lookup.ProductIds, cancellationToken)
            .ConfigureAwait(false);

        return ReceiptPackSizeHistory.Of(purchases);
    }

    private ScanReceiptResult InvalidFile(ReceiptScanFileVerdict verdict) =>
        ScanReceiptResult.Refused(
            ScanReceiptOutcome.InvalidFile,
            ReceiptScanFilePolicy.MessageFor(
                verdict,
                verdict is ReceiptScanFileVerdict.PdfTooLarge ? _settings.MaxPdfBytes : _settings.MaxImageBytes,
                _settings.MaxPdfPages));

    /// <summary>
    /// The file's bytes, read once into memory. A receipt is at most the endpoint's upload cap -
    /// eight million bytes, inside the purchase document ceiling of 10 MB - and the reader needs the
    /// whole of it, so there is nothing to stream to and nowhere to put a temporary copy.
    /// </summary>
    private static async Task<byte[]> ReadAsync(PurchaseFileInput file, CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }
}
