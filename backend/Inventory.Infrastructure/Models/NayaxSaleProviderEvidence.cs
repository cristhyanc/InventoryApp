using System.Text.Json.Serialization;
using Inventory.Domain.NayaxSqs;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One piece of evidence about what the business's own Nayax <c>lastSales</c> said about one stored
/// sale (issue #1104, child of #1100).
///
/// <para><strong>Append-only, and that is the whole point.</strong> A verification state is a
/// conclusion; this table is the observation the conclusion was drawn from. Nothing updates a row and
/// nothing deletes one: a later run that sees something different appends, so a sale that was
/// verified and is now conflicting still says what it was verified by. An audit trail that could be
/// rewritten is not one.</para>
///
/// <para><strong>Two kinds, with their own columns.</strong> A
/// <see cref="NayaxSaleProviderEvidenceKind.Comparison"/> row stores the <c>lastSales</c> item's
/// values <em>exactly as received</em> and each field's own comparison result. An
/// <see cref="NayaxSaleProviderEvidenceKind.Absence"/> row stores what the response for that machine
/// <em>did</em> return, the sale's own instants, and the two observations - bracketed, outside the
/// receipt grace - that made it worth recording. The columns of the other kind are null, which is
/// what keeps the two readable apart rather than collapsed into one shape that means different things
/// in different rows.</para>
///
/// <para><strong>No column says the provider's response was complete.</strong> Nayax documents
/// <c>lastSales</c> as "the most recent sales" for a machine - not how many, and not a window - so an
/// absence row records a count and two instants and claims nothing beyond them. There is deliberately
/// nothing here for a later reader to misread as completeness, and no code may treat an absence row
/// as proof that a sale did not happen. See
/// <c>Inventory.Domain.NayaxSqs.NayaxSaleProviderAbsencePolicy</c>.</para>
///
/// <para><strong><see cref="Id"/> is how issue #1105 names what an Owner decided about.</strong> An
/// Owner decision has to say which observation it acted on, which is why the row has a key of its own
/// and why nothing reuses or renumbers one.</para>
///
/// <para><strong>Tenant-owned, with nothing added</strong>, like every other table about one
/// business's own data: <c>AppDbContext</c> gives it the business key, its index, the central query
/// filter and the ownership stamp. It carries <em>no</em> foreign key to <c>NayaxSales</c>, for the
/// reason <see cref="PendingInventoryCostRebuild"/> carries none to <c>Products</c>: a restricted key
/// would make deleting a sale depend on its evidence, and a cascading one would delete the evidence a
/// financial decision was recorded against. The sale is this business's own by the ownership key the
/// row is read under.</para>
///
/// <para><strong>It records nothing financial of its own.</strong> Every value here is a copy of
/// something the provider said or something this application already stored; no quantity, cost or
/// profit is derived from it, and no financial rule reads it. In issue #1104 nothing reads it at all
/// except the deduplication that decides whether a repeated observation is worth another row.</para>
/// </summary>
public class NayaxSaleProviderEvidence : IBusinessOwned
{
    /// <summary>
    /// The row's own key, assigned by the database. It is what issue #1105's Owner decision names.
    /// </summary>
    public long Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The sale this is evidence about - <c>NayaxSales.Id</c>, this business's own.</summary>
    public long NayaxSaleId { get; set; }

    /// <summary>
    /// The reconciliation run that made the observation, so the rows one run produced are readable as
    /// one run. It is this application's own identifier for a run and is never a Nayax value.
    /// </summary>
    public Guid RunId { get; set; }

    /// <summary>When the run made the observation, from the application clock.</summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>Which of the two kinds of evidence this row is.</summary>
    public NayaxSaleProviderEvidenceKind Kind { get; set; }

    /// <summary>
    /// The verification state the sale held <em>after</em> this evidence was applied.
    ///
    /// <para>It is the resulting state rather than the state the evidence argued for, and the
    /// difference matters: an observation that argued for a state the transitions do not permit -
    /// matching evidence about a row already in <see cref="NayaxSqsVerification.Conflict"/> - is
    /// still recorded, and this column truthfully says the sale stayed conflicting.</para>
    /// </summary>
    public NayaxSqsVerification ResultingState { get; set; }

    /// <summary>
    /// The <c>MachineID</c> the <c>lastSales</c> item carried, on a comparison row.
    /// <see langword="null"/> on an absence row.
    /// </summary>
    public long? ProviderMachineId { get; set; }

    /// <summary>The item's <c>SettlementValue</c>, exactly as received - never rounded.</summary>
    public decimal? ProviderSettlementValue { get; set; }

    /// <summary>
    /// The item's <c>AuthorizationDateTimeGMT</c> as a UTC instant, or <see langword="null"/> when
    /// the payload carried no usable value.
    /// </summary>
    public DateTime? ProviderAuthorizationInstantUtc { get; set; }

    /// <summary>
    /// The item's <c>CurrencyCode</c>, exactly as received - never trimmed or upper-cased. The
    /// normalisation belongs to the comparison; this column has to say what the provider sent.
    /// </summary>
    public string? ProviderCurrencyCode { get; set; }

    /// <summary>
    /// The item's provider product id, when the payload carried one. The documented <c>lastSales</c>
    /// response has no product identifier field, so this is normally <see langword="null"/> and the
    /// product is recorded as not compared.
    /// </summary>
    public long? ProviderNayaxProductId { get; set; }

    /// <summary>
    /// The item's <c>ProductName</c>, kept as evidence only. It is never compared: a product name is
    /// a label both sides may render differently, and the product identity is what was asked about.
    /// </summary>
    public string? ProviderProductName { get; set; }

    /// <summary>How the machine ids compared. <see langword="null"/> on an absence row.</summary>
    public NayaxSaleFieldComparison? MachineComparison { get; set; }

    /// <summary>How the amounts compared, rounded to two places half away from zero.</summary>
    public NayaxSaleFieldComparison? AmountComparison { get; set; }

    /// <summary>How the authorization instants compared, within one second.</summary>
    public NayaxSaleFieldComparison? InstantComparison { get; set; }

    /// <summary>How the currencies compared, trimmed and upper-cased.</summary>
    public NayaxSaleFieldComparison? CurrencyComparison { get; set; }

    /// <summary>How the provider product ids compared, exactly.</summary>
    public NayaxSaleFieldComparison? ProductComparison { get; set; }

    /// <summary>
    /// The machine whose <c>lastSales</c> was read, on an absence row. <see langword="null"/> on a
    /// comparison row, where <see cref="ProviderMachineId"/> says what the item itself carried.
    /// </summary>
    public long? ReadMachineId { get; set; }

    /// <summary>
    /// The earliest authorization instant among the items the response returned for that machine, or
    /// <see langword="null"/> when none carried one.
    /// </summary>
    public DateTime? ReturnedEarliestInstantUtc { get; set; }

    /// <summary>The latest, on the same terms.</summary>
    public DateTime? ReturnedLatestInstantUtc { get; set; }

    /// <summary>How many items the response returned for that machine.</summary>
    public int? ReturnedItemCount { get; set; }

    /// <summary>The sale's own stored authorization instant.</summary>
    public DateTime? SaleAuthorizationInstantUtc { get; set; }

    /// <summary>When the queue reader accepted the message that stored the sale.</summary>
    public DateTime? SaleReceivedViaSqsAtUtc { get; set; }

    /// <summary>
    /// Whether the sale had been in this application's hands for longer than the receipt grace when
    /// the run started - the receipt-grace result.
    /// </summary>
    public bool? OutsideReceiptGrace { get; set; }

    /// <summary>
    /// Whether the sale's own instant lies <em>strictly</em> between the earliest and latest instants
    /// the response returned.
    ///
    /// <para><strong>This is not a completeness claim and must never be read as one.</strong> It says
    /// the sale sits inside the range the response demonstrably reached, which is the strongest thing
    /// the provider's documented contract supports - and still not evidence that the sale did not
    /// happen.</para>
    /// </summary>
    public bool? Bracketed { get; set; }
}
