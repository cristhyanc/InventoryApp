namespace InventoryApi.DTOs;

/// <summary>
/// One sale on <c>GET api/machines/{id}/latest-sales</c> (issue #768).
///
/// <para>The property set <em>is</em> the contract: this response is an allow-list of the fields the
/// Sales tab displays, and the fields the Nayax <c>lastSales</c> payload carries beside them - the
/// masked card number, the CLI, the payment-service transaction and provider ids, the card brand -
/// together with the transaction-level <c>ProductCostPrice</c> the shared DTO holds, are not
/// published here and are not carried into the Application result either. Adding one would be a
/// deliberate change to this record.</para>
///
/// <para>There is no transaction status, because the documented <c>lastSales</c> contract has none;
/// see <c>Inventory.Application.Machines.MachineLatestSale</c> and <c>docs/architecture.md</c>
/// § Machine latest sales.</para>
/// </summary>
public sealed record MachineLatestSaleResponse
{
    /// <summary>The provider's transaction identity, used only to key a row.</summary>
    public long TransactionId { get; init; }

    /// <summary>
    /// The sale instant as a true UTC instant, from Nayax's <c>AuthorizationDateTimeGMT</c>, or
    /// <c>null</c> when the payload carried no usable value. Clients render it in the current
    /// business's own IANA time zone (the one <c>GET /api/business/current</c> reports), never in the
    /// browser's accidental local zone.
    /// </summary>
    public DateTime? SaleDateTimeUtc { get; init; }

    /// <summary>The product name the provider reported, or <c>null</c> when it reported none.</summary>
    public string? ProductName { get; init; }

    /// <summary>The payment method the provider reported, or <c>null</c> when it reported none.</summary>
    public string? PaymentMethod { get; init; }

    /// <summary>
    /// The settled amount, or <c>null</c> for a caller without <c>Dashboard.ViewFinancials</c>
    /// (issue #698's pattern: the endpoint stays open and the figure is withheld). Never <c>0</c>
    /// for a withheld amount - a zero would be a financial statement about the machine, and a false
    /// one.
    /// </summary>
    public decimal? SaleAmount { get; init; }

    /// <summary>
    /// The currency the provider reported the amount in, or <c>null</c> when it reported none and
    /// for a caller the amount is withheld from - a currency with no amount beside it labels
    /// nothing.
    /// </summary>
    public string? CurrencyCode { get; init; }
}

/// <summary>
/// One machine's latest provider sales (issue #768), newest first, or the reason there are none to
/// show.
///
/// <para>An unusable or unconfigured provider is answered <c>200</c> with <c>isAvailable: false</c>
/// and a stable reason code, not an error status: it is a state of this business rather than a
/// failure of the call, and the empty list must never be read as "this machine has sold nothing". A
/// transport failure, a cancellation and the support-session refusal are <em>not</em> answered here
/// and keep their own status codes.</para>
/// </summary>
public sealed record MachineLatestSalesResponse
{
    /// <summary>
    /// The machine these sales are for, echoed from the route so a client can discard an answer
    /// that arrived after it navigated to a different machine.
    /// </summary>
    public long MachineId { get; init; }

    /// <summary>
    /// Whether this is an answer about the machine's sales at all. <c>false</c> only when the
    /// provider could not be read.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// The stable reason code for an unavailable result - <c>NoSalesProvider</c>,
    /// <c>NayaxNotConnected</c> or <c>PermissionNotGranted</c> - and <c>null</c> for an available
    /// one. A code a client branches on, never a sentence to display.
    /// </summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The one sentence that explains an unavailable result; <c>null</c> for an available one.</summary>
    public string? UnavailableDetail { get; init; }

    /// <summary>
    /// The sales Nayax currently reports for this machine, newest first. It is the provider's own
    /// rolling latest-sales window: this response carries no date range, row count or completeness
    /// guarantee, because the documented endpoint makes none.
    /// </summary>
    public IReadOnlyList<MachineLatestSaleResponse> Sales { get; init; } = [];
}
