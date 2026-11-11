using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// One value of a scanned draft, with where it came from (issue #1163). Every value the draft
/// carries is wrapped, because an operator correcting a draft needs to know which figures are
/// quotations from the receipt and which are this application's suggestions.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Provenance">Whether it was read off the receipt or suggested here.</param>
public sealed record ReceiptScanValue<T>(T Value, ReceiptValueProvenance Provenance)
{
    /// <summary>A value printed on the receipt and read off it.</summary>
    public static ReceiptScanValue<T> Read(T value) => new(value, ReceiptValueProvenance.Read);

    /// <summary>A value this application matched or calculated.</summary>
    public static ReceiptScanValue<T> Suggested(T value) => new(value, ReceiptValueProvenance.Suggested);
}

/// <summary>
/// What the receipt says about one line's GST (issue #1163): the two tri-states the arithmetic needs,
/// and the marker the receipt printed, so an operator can see why.
/// </summary>
/// <param name="PriceBasis">Whether the printed price includes GST.</param>
/// <param name="Taxability">Whether the receipt marks the line as taxable.</param>
/// <param name="Marker">
/// The GST marker the reader saw, as printed - a <c>*</c>, a <c>GST</c> column heading, a legend
/// line - or <see langword="null"/> when it saw none.
/// </param>
public sealed record ReceiptLineGstHint(
    ReceiptPriceGstBasis PriceBasis,
    ReceiptLineTaxability Taxability,
    string? Marker);

/// <summary>
/// Where a pack size this application suggested came from (issue #1165, sub-issue E of #975), and
/// which purchase said so.
///
/// <para>It appears only beside a <strong>suggested</strong> pack size. A pack size printed on the
/// receipt carries no source, because it was not suggested by anything.</para>
/// </summary>
/// <param name="UnitsPerPack">The suggested pack size, the same figure the line publishes.</param>
/// <param name="SuggestedFrom">Which deterministic step suggested it.</param>
/// <param name="PurchaseDate">
/// The date of the purchase it was derived from, so an operator can see which purchase the number
/// came from and judge whether it still applies.
/// </param>
public sealed record ReceiptPackSizeSuggestion(
    int UnitsPerPack,
    ReceiptPackSizeSource SuggestedFrom,
    DateOnly PurchaseDate);

/// <summary>
/// One line of a scanned draft (issue #1163).
///
/// <para><strong>Delivery and package lines are not here.</strong> They are summed into the draft's
/// own delivery and package costs, which is where this application records them. A levy line is here
/// only when the totals could not say whether it is already inside the drink prices, and then it is
/// flagged.</para>
/// </summary>
/// <param name="Kind">What the line is. Only <see cref="ReceiptLineKind.Stock"/> carries a product suggestion.</param>
/// <param name="Text">The line as printed, which is what an operator recognises it by.</param>
/// <param name="Packs">How many packs were delivered.</param>
/// <param name="UnitsPerPack">
/// How many units one pack holds: as printed, or - when the receipt printed none - the pack size
/// issue #1165 suggested from the last purchase, marked <see cref="ReceiptValueProvenance.Suggested"/>
/// and described by <paramref name="UnitsPerPackSuggestion"/>. Never guessed from product knowledge.
/// </param>
/// <param name="Units">The product of the two above, when both are known.</param>
/// <param name="LineTotal">The line's GST-inclusive amount, after discounts and any levy share.</param>
/// <param name="UnitCost">The amount over the units, unrounded.</param>
/// <param name="ProductId">The suggested product of this business, when one was matched.</param>
/// <param name="Gst">What the receipt says about the line's GST.</param>
/// <param name="LevyUnresolved">
/// Whether this is a levy the totals could not place, which is the one thing on the draft that asks
/// the operator to decide rather than to check.
/// </param>
/// <param name="Missing">Which values the line could not be completed with.</param>
/// <param name="UnitsPerPackSuggestion">
/// Where a suggested pack size came from (issue #1165), or <see langword="null"/> when the pack size
/// was printed on the receipt or is still unknown.
/// </param>
public sealed record ReceiptScanLine(
    ReceiptLineKind Kind,
    ReceiptScanValue<string> Text,
    ReceiptScanValue<int>? Packs,
    ReceiptScanValue<int>? UnitsPerPack,
    ReceiptScanValue<int>? Units,
    ReceiptScanValue<decimal>? LineTotal,
    ReceiptScanValue<decimal>? UnitCost,
    ReceiptScanValue<long>? ProductId,
    ReceiptLineGstHint Gst,
    bool LevyUnresolved,
    IReadOnlyList<ReceiptLineMissingValue> Missing,
    ReceiptPackSizeSuggestion? UnitsPerPackSuggestion = null);

/// <summary>
/// A purchase this application suggests from one receipt (issue #1163). <strong>Nothing about it is
/// stored</strong>: no file, no draft, no purchase. It is returned to the caller and forgotten.
/// </summary>
/// <param name="SupplierText">The supplier as printed on the receipt.</param>
/// <param name="SupplierId">The suggested supplier of this business, when one was matched.</param>
/// <param name="PurchaseDate">
/// The delivery date when the receipt printed one, otherwise its transaction or invoice date (never
/// the order date - see <c>ReceiptPurchaseDatePolicy</c>).
/// </param>
/// <param name="InvoiceNumber">The supplier's invoice or order number as printed (issue #1162's field).</param>
/// <param name="ReceiptTotal">The total printed on the receipt.</param>
/// <param name="GstTotal">The GST total, when the receipt printed one.</param>
/// <param name="DeliveryCost">The delivery, freight and shipping charges, summed.</param>
/// <param name="PackageCost">The bag and packaging charges, summed.</param>
/// <param name="Lines">The stock, other and unresolved-levy lines.</param>
public sealed record ReceiptScanDraft(
    ReceiptScanValue<string>? SupplierText,
    ReceiptScanValue<int>? SupplierId,
    ReceiptScanValue<DateOnly>? PurchaseDate,
    ReceiptScanValue<string>? InvoiceNumber,
    ReceiptScanValue<decimal>? ReceiptTotal,
    ReceiptScanValue<decimal>? GstTotal,
    ReceiptScanValue<decimal>? DeliveryCost,
    ReceiptScanValue<decimal>? PackageCost,
    IReadOnlyList<ReceiptScanLine> Lines);

/// <summary>How a scan attempt ended (issue #1163).</summary>
public enum ScanReceiptOutcome
{
    /// <summary>A draft was produced.</summary>
    Draft = 10,

    /// <summary>
    /// Receipt scanning is not configured in this deployment, which is the shipped state. No
    /// outbound call is made.
    /// </summary>
    SwitchedOff = 20,

    /// <summary>The file was refused, and the reader was not called.</summary>
    InvalidFile = 30,

    /// <summary>
    /// The reader answered, and nothing in its answer matched the published schema. Nothing partial
    /// is returned, because a draft with no lines is not a reading of a receipt.
    /// </summary>
    Unreadable = 40,

    /// <summary>The reader did not answer within the configured timeout. Exactly one call was made.</summary>
    TimedOut = 50,

    /// <summary>The reader could not be used.</summary>
    Unavailable = 60,

    /// <summary>
    /// The business has used its whole month's allowance of scans (issue #1164), so the reader was
    /// not called. It is a refusal about the month rather than about the receipt: next month's
    /// allowance starts at zero on its own, in the business's own calendar.
    /// </summary>
    LimitReached = 70,
}

/// <summary>
/// A scan attempt's answer (issue #1163): one outcome, a draft only for
/// <see cref="ScanReceiptOutcome.Draft"/>, and a caller-safe message for everything else.
///
/// <para><see cref="Usage"/> is issue #1164's addition: how much of the month's allowance a
/// successful scan has now used, so the operator sees the count move on the answer rather than
/// having to ask for it separately. It is present on a draft and absent on every refusal, including
/// the one the cap itself produces - a refused scan is not counted, so there is no new count to
/// report, and the refusal names the cap in its own sentence.</para>
/// </summary>
public sealed record ScanReceiptResult(
    ScanReceiptOutcome Outcome,
    ReceiptScanDraft? Draft,
    string? Message,
    ReceiptScanUsageView? Usage = null)
{
    /// <summary>What a caller is told when scanning is not configured in this deployment.</summary>
    public const string SwitchedOffMessage = "Receipt scanning is not available.";

    /// <summary>What a caller is told when the reader's answer held nothing usable.</summary>
    public const string UnreadableMessage = "Could not read this receipt.";

    /// <summary>What a caller is told when the reader did not answer in time.</summary>
    public const string TimedOutMessage =
        "Reading the receipt took too long. Try again, or enter the purchase manually.";

    /// <summary>What a caller is told when the reader could not be used.</summary>
    public const string UnavailableMessage =
        "Receipt scanning is temporarily unavailable. Try again later, or enter the purchase manually.";

    /// <summary>The successful answer, with the month's allowance as it now stands (issue #1164).</summary>
    public static ScanReceiptResult Scanned(ReceiptScanDraft draft, ReceiptScanUsageView usage) =>
        new(ScanReceiptOutcome.Draft, draft, null, usage);

    /// <summary>A refusal, with the message for that outcome.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="ScanReceiptOutcome.Draft"/>, which is not a refusal.
    /// </exception>
    public static ScanReceiptResult Refused(ScanReceiptOutcome outcome, string? message = null) =>
        outcome switch
        {
            ScanReceiptOutcome.SwitchedOff => new(outcome, null, SwitchedOffMessage),
            ScanReceiptOutcome.InvalidFile => new(
                outcome,
                null,
                message ?? throw new ArgumentNullException(
                    nameof(message), "A refused file is refused for a stated reason.")),
            ScanReceiptOutcome.Unreadable => new(outcome, null, UnreadableMessage),
            ScanReceiptOutcome.TimedOut => new(outcome, null, TimedOutMessage),
            ScanReceiptOutcome.Unavailable => new(outcome, null, UnavailableMessage),
            // Issue #1164. The sentence names the cap it was refused by, from the one place the cap
            // is written, so the message cannot say one number while another is enforced.
            ScanReceiptOutcome.LimitReached => new(
                outcome,
                null,
                message ?? throw new ArgumentNullException(
                    nameof(message), "A monthly-cap refusal states the cap it was refused by.")),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "A draft is not a refusal."),
        };
}
