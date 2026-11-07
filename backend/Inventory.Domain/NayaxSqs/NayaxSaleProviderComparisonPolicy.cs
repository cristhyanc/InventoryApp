namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What one field's comparison answered (issue #1104). Three answers and never two: a value that
/// could not be compared is its own outcome, so "verified" can mean "these specific fields were
/// compared and were equal" rather than "nothing disagreed".
///
/// <para>No value is <c>0</c>, for the reason <see cref="NayaxSqsVerification"/> declares none: this
/// is persisted on an evidence row, and an unfilled column must not look like an answer somebody
/// recorded.</para>
/// </summary>
public enum NayaxSaleFieldComparison
{
    /// <summary>
    /// The evidence for this field was missing on one side or on both, so no comparison was made.
    /// It is <strong>never</strong> counted as a match.
    /// </summary>
    NotCompared = 10,

    /// <summary>Both sides carried a value and, after normalisation, they were equal.</summary>
    Match = 20,

    /// <summary>Both sides carried a value and, after normalisation, they differed.</summary>
    Mismatch = 30,
}

/// <summary>
/// The stored sale's own side of the comparison, as this application holds it (issue #1104).
///
/// <para><strong><see cref="CurrencyCode"/> has no stored column today</strong>, so in practice it
/// arrives <see langword="null"/> and the currency is recorded as
/// <see cref="NayaxSaleFieldComparison.NotCompared"/>. It is a parameter rather than an omission
/// because the normalisation is the owner's own rule and belongs in one place: the day a sale
/// records the currency it was conducted in, the comparison is already written and already tested.
/// Issue #1104 adds no column to a sale.</para>
/// </summary>
/// <param name="MachineId">The stored <c>MachineID</c>.</param>
/// <param name="SettlementValue">The stored settlement amount.</param>
/// <param name="AuthorizationInstantUtc">
/// The stored authorization instant, which is a true UTC instant (issue #380), or
/// <see langword="null"/> for a row that holds none.
/// </param>
/// <param name="CurrencyCode">The currency the sale was conducted in, if this application holds one.</param>
/// <param name="NayaxProductId">
/// The provider's own product identifier as stored, or <see langword="null"/> when the row carries
/// none. Never the local product id - that is not a value Nayax could agree or disagree with.
/// </param>
public sealed record NayaxStoredSaleValues(
    long MachineId,
    decimal SettlementValue,
    DateTime? AuthorizationInstantUtc,
    string? CurrencyCode,
    long? NayaxProductId);

/// <summary>
/// The <c>lastSales</c> item that carried the same <c>TransactionID</c>, exactly as received (issue
/// #1104). It is stored on the evidence row unchanged, so what the provider said is readable later
/// rather than only what this application concluded from it.
///
/// <para>The field names follow the documented
/// <c>GET /v1/machines/{MachineID}/lastSales</c> response
/// (https://devzone.nayax.com/reference/lynx/machines/get-last-sales-for-machine-by-machineid):
/// <c>MachineID</c>, <c>SettlementValue</c>, <c>CurrencyCode</c>, <c>AuthorizationDateTimeGMT</c>
/// and <c>ProductName</c>. The documented response carries <strong>no product identifier</strong>
/// and <strong>no transaction status</strong>, so <see cref="NayaxProductId"/> is
/// <see langword="null"/> for every item the documented contract describes and the product is
/// recorded as not compared; the parameter exists because this application's <c>lastSales</c> model
/// has carried that property since before this issue, and a value that is there must be compared
/// rather than ignored.</para>
/// </summary>
/// <param name="MachineId">The item's own <c>MachineID</c>.</param>
/// <param name="SettlementValue">The item's <c>SettlementValue</c>.</param>
/// <param name="AuthorizationInstantUtc">
/// The item's <c>AuthorizationDateTimeGMT</c> as a UTC instant, or <see langword="null"/> when the
/// payload carried no usable value.
/// </param>
/// <param name="CurrencyCode">The item's <c>CurrencyCode</c>, documented <c>string | null</c>.</param>
/// <param name="NayaxProductId">The item's provider product id, when the payload carried one.</param>
/// <param name="ProductName">
/// The item's <c>ProductName</c>, kept as evidence only. It is never compared: a product name is a
/// label both sides may render differently, and the product identity is what the comparison is about.
/// </param>
public sealed record NayaxProviderSaleValues(
    long MachineId,
    decimal SettlementValue,
    DateTime? AuthorizationInstantUtc,
    string? CurrencyCode,
    long? NayaxProductId,
    string? ProductName);

/// <summary>Each compared field's own answer (issue #1104), recorded on the evidence row.</summary>
public sealed record NayaxSaleFieldComparisons(
    NayaxSaleFieldComparison Machine,
    NayaxSaleFieldComparison Amount,
    NayaxSaleFieldComparison Instant,
    NayaxSaleFieldComparison Currency,
    NayaxSaleFieldComparison Product);

/// <summary>
/// What comparing one stored sale with its <c>lastSales</c> item decided (issue #1104).
/// </summary>
/// <param name="Fields">Each field's own answer, which is recorded whatever the state came out as.</param>
/// <param name="ObservedState">
/// The state this comparison is evidence for, or <see langword="null"/> when it is evidence for none
/// - which is exactly the case where the authorization instant could not be compared, because
/// <see cref="NayaxSqsVerification.Verified"/> requires it and nothing was contradicted either. The
/// sale then keeps the state it had and the evidence is still recorded.
/// </param>
public sealed record NayaxSaleComparisonResult(
    NayaxSaleFieldComparisons Fields,
    NayaxSqsVerification? ObservedState);

/// <summary>
/// "Does the business's own <c>lastSales</c> agree with the sale the queue delivered?" - the one
/// place that question is answered (issue #1104, child of #1100).
///
/// <para><strong>Both sides have to carry a value for a field to be compared at all.</strong> That
/// is the rule the whole model rests on: a sale is <see cref="NayaxSqsVerification.Verified"/> only
/// because specific fields were compared and were equal, so a missing value has to be its own
/// recorded answer rather than silent agreement. The machine and the amount are always present on
/// both sides and are therefore always compared; the instant, the currency and the product are
/// compared only when both sides have one.</para>
///
/// <para><strong>A contradiction and a flag are different things.</strong> A different machine,
/// amount or product is an authoritative contradiction about <em>what was sold</em>, which is
/// <see cref="NayaxSqsVerification.Conflict"/> and is terminal for this reconciliation. A different
/// instant or currency is something a person should look at and is
/// <see cref="NayaxSqsVerification.NeedsInvestigation"/>: the two records can genuinely disagree
/// about when a transaction was authorized or in what currency it is reported while agreeing
/// completely about the sale itself.</para>
///
/// <para><strong>It is a pure function and it writes nothing.</strong> Nothing here reads a
/// database, a clock or a configuration value, and the normalisations are fixed constants the issue
/// named rather than settings: this is a financial comparison, and a tolerance somebody could widen
/// at run time is a tolerance nobody can reason about.</para>
/// </summary>
public static class NayaxSaleProviderComparisonPolicy
{
    /// <summary>
    /// How far apart two authorization instants may be and still be the same authorization: one
    /// second, the repository owner's value of 5 November 2026.
    ///
    /// <para>It is a tolerance rather than an exact match because the two records reach this
    /// application by different routes - a queue message and an HTTP response - and sub-second
    /// rounding on either side is not a disagreement about anything. It is small enough that two
    /// genuinely different authorizations cannot both satisfy it.</para>
    /// </summary>
    public static readonly TimeSpan InstantTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many decimal places an amount is compared at: two, the currency's own minor unit.
    /// </summary>
    public const int AmountDecimalPlaces = 2;

    /// <summary>
    /// The rounding the amounts are compared under:
    /// <see cref="MidpointRounding.AwayFromZero"/>, which is the application's own convention for
    /// money and the only one that treats a half-cent the same way in both directions.
    /// </summary>
    public const MidpointRounding AmountRounding = MidpointRounding.AwayFromZero;

    /// <summary>
    /// Compares one stored sale with the <c>lastSales</c> item carrying the same
    /// <c>TransactionID</c>.
    /// </summary>
    public static NayaxSaleComparisonResult Compare(
        NayaxStoredSaleValues stored, NayaxProviderSaleValues provider)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(provider);

        var fields = new NayaxSaleFieldComparisons(
            Machine: Compared(stored.MachineId == provider.MachineId),
            Amount: Compared(Money(stored.SettlementValue) == Money(provider.SettlementValue)),
            Instant: CompareInstants(stored.AuthorizationInstantUtc, provider.AuthorizationInstantUtc),
            Currency: CompareCurrencies(stored.CurrencyCode, provider.CurrencyCode),
            Product: CompareProducts(stored.NayaxProductId, provider.NayaxProductId));

        return new NayaxSaleComparisonResult(fields, StateOf(fields));
    }

    /// <summary>
    /// Which state these field answers are evidence for, in the one order that keeps each state's
    /// meaning: a contradiction about what was sold outranks a field that only needs looking at,
    /// which outranks agreement.
    ///
    /// <para>The last case is the one worth naming: with the machine and the amount equal but the
    /// instant <em>not compared</em>, nothing has been contradicted and nothing has been verified
    /// either, so this comparison is evidence for no state at all. Treating it as verified would make
    /// <see cref="NayaxSqsVerification.Verified"/> mean less than it says.</para>
    /// </summary>
    private static NayaxSqsVerification? StateOf(NayaxSaleFieldComparisons fields)
    {
        if (fields.Machine is NayaxSaleFieldComparison.Mismatch
            || fields.Amount is NayaxSaleFieldComparison.Mismatch
            || fields.Product is NayaxSaleFieldComparison.Mismatch)
        {
            return NayaxSqsVerification.Conflict;
        }

        if (fields.Instant is NayaxSaleFieldComparison.Mismatch
            || fields.Currency is NayaxSaleFieldComparison.Mismatch)
        {
            return NayaxSqsVerification.NeedsInvestigation;
        }

        return fields.Machine is NayaxSaleFieldComparison.Match
            && fields.Amount is NayaxSaleFieldComparison.Match
            && fields.Instant is NayaxSaleFieldComparison.Match
                ? NayaxSqsVerification.Verified
                : null;
    }

    private static NayaxSaleFieldComparison Compared(bool equal) =>
        equal ? NayaxSaleFieldComparison.Match : NayaxSaleFieldComparison.Mismatch;

    private static decimal Money(decimal value) =>
        Math.Round(value, AmountDecimalPlaces, AmountRounding);

    private static NayaxSaleFieldComparison CompareInstants(DateTime? stored, DateTime? provider)
    {
        if (stored is not { } left || provider is not { } right)
        {
            return NayaxSaleFieldComparison.NotCompared;
        }

        // Both are UTC instants by the time they reach here - the stored column is one (issue #380)
        // and the payload value is converted from the documented GMT field (issue #471) - so the
        // difference is an instant difference and not a wall-clock one.
        return Compared((left - right).Duration() <= InstantTolerance);
    }

    private static NayaxSaleFieldComparison CompareCurrencies(string? stored, string? provider)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(provider))
        {
            // A blank is not a currency. Reading one as a value would make two sides that both know
            // nothing agree with each other.
            return NayaxSaleFieldComparison.NotCompared;
        }

        return Compared(string.Equals(Currency(stored), Currency(provider), StringComparison.Ordinal));
    }

    /// <summary>
    /// Trimmed and upper-cased with the invariant culture, so the comparison answers the same on
    /// every host: a culture-sensitive upper-casing turns a Turkish <c>i</c> into a different letter,
    /// and a currency code is an ASCII identifier rather than text in anybody's language.
    /// </summary>
    private static string Currency(string value) =>
        value.Trim().ToUpperInvariant();

    private static NayaxSaleFieldComparison CompareProducts(long? stored, long? provider) =>
        stored is { } left && provider is { } right
            ? Compared(left == right)
            : NayaxSaleFieldComparison.NotCompared;
}
