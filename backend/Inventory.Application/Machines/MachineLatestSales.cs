namespace Inventory.Application.Machines;

/// <summary>
/// One sale in a machine's latest-sales window (issue #768), holding only the fields the machine
/// page displays.
///
/// <para><strong>This is an allow-list, not a projection of the payload.</strong> The Nayax
/// <c>GET /v1/machines/{MachineID}/lastSales</c> response also carries the masked card number, the
/// CLI, the payment-service transaction and provider ids and the card brand, and the shared
/// <see cref="Nayax.NayaxLastSalesReport"/> additionally carries a transaction-level
/// <c>ProductCostPrice</c>. None of them is on this record, so none of them can reach an API
/// response, a log or a client by anybody forgetting to withhold it: the only way to publish one
/// would be to add it here deliberately.</para>
///
/// <para><strong>No transaction status.</strong> The documented <c>lastSales</c> response has no
/// status field at all - verified against the Nayax developer portal's reference for that endpoint
/// on 18 October 2026 - so this read reports none. The nullable
/// <c>NayaxLastSalesReport.TransactionStatusId</c> the sales synchronization reads is not part of
/// the documented contract for this endpoint, and inventing a status from it would be a financial
/// statement (approved, refunded, cancelled) that the authority does not make. See
/// <c>docs/architecture.md</c> § Machine latest sales.</para>
/// </summary>
/// <param name="TransactionId">
/// The provider's own transaction identity (<c>TransactionID</c>, documented
/// <c>integer&lt;int64&gt;</c>, non-nullable). An external identity used here only to key a table
/// row; it is never read as a local key.
/// </param>
/// <param name="SaleAtUtc">
/// The authoritative sale instant, from <c>AuthorizationDateTimeGMT</c> through
/// <see cref="Nayax.NayaxLastSalesReport.AuthorizationInstantUtc"/> - the one normalization of a
/// Nayax timestamp into a true UTC instant (issue #471). <see langword="null"/> when the payload
/// carried no usable value, which stays distinguishable from a sale at
/// <see cref="DateTime.MinValue"/>; the machine-local <c>MachineAuthorizationTime</c> is never read
/// as an instant and is not on this record at all.
/// </param>
/// <param name="ProductName">
/// The product name the provider reported for the transaction (<c>ProductName</c>, documented
/// <c>string | null</c>), or <see langword="null"/> when it reported none. It is the provider's own
/// text, not a local catalogue product: this read resolves no product and derives no cost or profit
/// from it.
/// </param>
/// <param name="PaymentMethod">
/// The payment method the provider reported (<c>PaymentMethod</c>, documented
/// <c>string | null</c>), or <see langword="null"/> when it reported none.
/// </param>
/// <param name="SaleAmount">
/// The settled amount (<c>SettlementValue</c>, documented <c>number&lt;double&gt;</c> and
/// non-nullable, which is also how the shared DTO declares it). Because the contract declares it
/// required, a <c>0</c> here is a reported zero rather than an absent value.
/// </param>
/// <param name="CurrencyCode">
/// The currency the provider reported the amount in (<c>CurrencyCode</c>, documented
/// <c>string | null</c>), or <see langword="null"/> when it reported none. Never defaulted to a
/// currency this application assumes.
/// </param>
public sealed record MachineLatestSale(
    long TransactionId,
    DateTime? SaleAtUtc,
    string? ProductName,
    string? PaymentMethod,
    decimal SaleAmount,
    string? CurrencyCode);

/// <summary>
/// Why a machine's latest Nayax sales could not be read for this business right now (issue #768).
///
/// <para>The last two are the per-feature availability answers issue #726 established for reorder
/// and issue #731 reused for site commission, published under the same codes so a client meets one
/// vocabulary. The first is the distinct state issue #759 made possible: a business with no sales
/// provider at all, which is a supported way to run this application rather than a fault, and is
/// therefore never presented as a failure.</para>
///
/// <para>Numbered from 1, so no reason is reachable by forgetting to set one.</para>
/// </summary>
public enum MachineLatestSalesUnavailableReason
{
    /// <summary>
    /// This business has no sales provider configured at all (issue #759): nothing is stored for it,
    /// which is exactly what <c>NayaxConnectionStatus.NotConfigured</c> means and is the one state
    /// a business that has never connected a provider is in - storing credentials is the single
    /// thing that claims <c>Business.SalesProvider</c>. Inventory works fully without one, so this
    /// is an explanation, not an error, and there is nothing for an operator to repair.
    /// </summary>
    NoSalesProvider = 1,

    /// <summary>
    /// The business has a provider but no credentials an ordinary operation may use: a connection
    /// the status gate refuses (<c>NeedsAttention</c>), including one a <c>401</c> has just moved
    /// there. An operator has to reconnect Nayax.
    /// </summary>
    NayaxNotConnected = 2,

    /// <summary>
    /// The credentials are usable and Nayax refused this read for lack of permission (<c>403</c>).
    /// One feature's answer, not a verdict on the connection, so nothing here writes a connection
    /// status.
    /// </summary>
    PermissionNotGranted = 3,

    /// <summary>
    /// The business's effective plan does not include the sales provider (issue #967), so the
    /// credential gate refused before the stored connection was read and no Nayax request was made.
    /// Distinct from <see cref="NayaxNotConnected"/> and from <see cref="NoSalesProvider"/>: the
    /// business may have valid credentials saved and a provider claimed, and what would show these
    /// sales again is the plan.
    /// </summary>
    PlanRequired = 4,
}

/// <summary>
/// The stable codes <see cref="MachineLatestSalesUnavailableReason"/> is published under, written
/// out rather than taken from the enum member so renaming a C# member cannot silently change the
/// API contract. <see cref="NayaxNotConnected"/> and <see cref="PermissionNotGranted"/> are
/// deliberately spelled exactly as the reorder and site-commission surfaces spell the same two
/// answers.
/// </summary>
public static class MachineLatestSalesUnavailableReasons
{
    /// <summary>The published code for <see cref="MachineLatestSalesUnavailableReason.NoSalesProvider"/>.</summary>
    public const string NoSalesProvider = "NoSalesProvider";

    /// <summary>The published code for <see cref="MachineLatestSalesUnavailableReason.NayaxNotConnected"/>.</summary>
    public const string NayaxNotConnected = "NayaxNotConnected";

    /// <summary>The published code for <see cref="MachineLatestSalesUnavailableReason.PermissionNotGranted"/>.</summary>
    public const string PermissionNotGranted = "PermissionNotGranted";

    /// <summary>
    /// The published code for <see cref="MachineLatestSalesUnavailableReason.PlanRequired"/> (issue
    /// #967), spelled exactly as every other per-feature vocabulary spells the same answer.
    /// </summary>
    public const string PlanRequired = "PlanRequired";

    /// <exception cref="ArgumentOutOfRangeException">
    /// A reason this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published under a guessed code.
    /// </exception>
    public static string Code(MachineLatestSalesUnavailableReason reason) => reason switch
    {
        MachineLatestSalesUnavailableReason.NoSalesProvider => NoSalesProvider,
        MachineLatestSalesUnavailableReason.NayaxNotConnected => NayaxNotConnected,
        MachineLatestSalesUnavailableReason.PermissionNotGranted => PermissionNotGranted,
        MachineLatestSalesUnavailableReason.PlanRequired => PlanRequired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown machine latest-sales unavailability reason."),
    };

    /// <summary>
    /// The one sentence each reason is explained with. None of them claims the machine made no
    /// sales, and none of them promises a window this read does not know the size of.
    /// </summary>
    public static string Describe(MachineLatestSalesUnavailableReason reason) => reason switch
    {
        MachineLatestSalesUnavailableReason.NoSalesProvider =>
            "This business has no sales provider connected, so there are no provider sales to show for this machine. Inventory and purchasing are unaffected.",
        MachineLatestSalesUnavailableReason.NayaxNotConnected =>
            "Latest sales are unavailable because Nayax is not connected for this business. Reconnect this business's Nayax account to see them. This is not a report that the machine made no sales.",
        MachineLatestSalesUnavailableReason.PermissionNotGranted =>
            "Latest sales are unavailable because Nayax refused this machine's sales for lack of permission. Check the Nayax account's permissions for this feature. This is not a report that the machine made no sales.",
        MachineLatestSalesUnavailableReason.PlanRequired =>
            "Latest sales are unavailable because this business's plan does not include the Nayax connection. Any credentials this business has saved are unchanged. This is not a report that the machine made no sales.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown machine latest-sales unavailability reason."),
    };
}

/// <summary>
/// One machine's latest provider sales, newest first, or the reason there are none to show (issue
/// #768).
/// </summary>
public sealed record MachineLatestSales(IReadOnlyList<MachineLatestSale> Sales)
{
    /// <summary>
    /// Whether this is an answer about the machine's sales at all. <see langword="true"/> for every
    /// result built from a provider read that succeeded, including one that legitimately returned
    /// no sale. <see langword="false"/> only when the provider could not be read, where the empty
    /// list means "nothing could be read" and must never be shown as "this machine has sold
    /// nothing".
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// Why it is unavailable, for an unavailable result, and <see langword="null"/> for an available
    /// one - where there is nothing to explain.
    /// </summary>
    public MachineLatestSalesUnavailableReason? UnavailableReason { get; init; }

    /// <summary>
    /// The unavailable answer: no sales at all, and the reason.
    ///
    /// <para>A factory rather than an object initializer so an unavailable result cannot be built
    /// holding a sale - the empty list is the only shape this answer has - and so the reason is not
    /// optional in the one case that needs it.</para>
    /// </summary>
    public static MachineLatestSales Unavailable(MachineLatestSalesUnavailableReason reason) =>
        new([])
        {
            IsAvailable = false,
            UnavailableReason = reason,
        };
}
