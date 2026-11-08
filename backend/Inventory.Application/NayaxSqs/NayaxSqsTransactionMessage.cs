using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// One Nayax SQS transaction message, as far as this application reads it (issue #1102).
///
/// <para>Nayax Core delivers a transaction to the queue as a two-layer document: root fields that
/// identify the transaction and the machine, and a <c>Data</c> object whose keys contain spaces and
/// whose <c>Products</c> array lists each item vended. The shape and every field name below are the
/// published contract, checked against the Nayax developer portal's Transaction JSON reference
/// (<c>docs/manage-data-operations/amazon-sqs/set-up-nayax-core/transaction-json-reference</c>) on
/// 5 November 2026; nothing here is inferred from a field name.</para>
///
/// <para><strong>These three types are an allow-list, and that is their whole point.</strong> The
/// message carries the consumer's card and identity - <c>Card String</c>, the card digit fields,
/// <c>Display Card Number</c>, <c>Card Holder Name</c>, <c>Card Type</c>, <c>Consumer ID</c>,
/// <c>Token</c>, <c>Zip Code</c>, <c>User Password</c> and the discount card fields - and this
/// application has no use for any of it. A member declared here is a field the parser maps or
/// classifies by; everything else in the document is never bound to a model, so it cannot reach an
/// outcome, a log, a reason or an exception message by accident later. Unknown extra keys are
/// ignored, which is also what lets Nayax Core's selectable columns arrive without a code
/// change.</para>
///
/// <para><strong>Every scalar member is a <see cref="JsonElement"/> rather than its natural
/// type</strong>, because a wrong type in a financial payload must become a stated outcome rather
/// than an exception: <c>"SeValue": "1.00"</c> is an <em>invalid message</em> with a reason code, and
/// must never bind to <c>0</c>, to <c>null</c>, or to a thrown <see cref="JsonException"/> whose text
/// quotes the payload. <see cref="NayaxSqsTransactionParser"/> reads each member through
/// <c>NayaxSqsJsonValues</c>, which answers a value only for the JSON kind the contract documents, so
/// the only way to fail the deserialization itself is a body that is not JSON at all.</para>
/// </summary>
public sealed class NayaxSqsTransactionMessage
{
    /// <summary>The transaction's identity, documented as an integer at the root.</summary>
    [JsonPropertyName("TransactionId")]
    public JsonElement? TransactionId { get; set; }

    /// <summary>The Nayax machine id, documented as an integer at the root.</summary>
    [JsonPropertyName("MachineId")]
    public JsonElement? MachineId { get; set; }

    /// <summary>
    /// The root <c>Void</c> flag. It appears in the published example message and the reference's
    /// field table does not describe it, so a message carrying <c>true</c> is unsupported rather
    /// than interpreted (issue #1102; parent #1100's rollout check records what it really means).
    /// </summary>
    [JsonPropertyName("Void")]
    public JsonElement? IsVoid { get; set; }

    /// <summary>
    /// The transaction detail object. Read as an element rather than a nested model so that a
    /// <c>Data</c> which is absent, null or not an object is a stated invalid outcome instead of a
    /// binding failure.
    /// </summary>
    [JsonPropertyName("Data")]
    public JsonElement? Data { get; set; }
}

/// <summary>
/// The allow-listed fields of a message's <c>Data</c> object. See
/// <see cref="NayaxSqsTransactionMessage"/> for why the members are
/// <see cref="JsonElement"/>s and why nothing else in the object is declared.
/// </summary>
public sealed class NayaxSqsTransactionData
{
    /// <summary>
    /// <c>Transaction ID</c>, documented as "Transaction ID (Nayax Internal Identifier)" - the same
    /// identifier the root carries, repeated inside <c>Data</c> as the first real message repeats
    /// it. It is read for one purpose only: a message whose two copies disagree is not a message
    /// about one transaction, and is refused rather than saved under either value.
    /// </summary>
    [JsonPropertyName("Transaction ID")]
    public JsonElement? TransactionId { get; set; }

    /// <summary>
    /// <c>Machine ID</c>, documented as "Machine ID, automatically generated Nayax internal ID for
    /// machine", repeated inside <c>Data</c> like the transaction id above and read on the same
    /// terms.
    /// </summary>
    [JsonPropertyName("Machine ID")]
    public JsonElement? MachineId { get; set; }

    /// <summary>
    /// <c>SeValue</c>, documented as "Settlement Value, the amount charged". The amount is the one
    /// field a missing or malformed value may never default: an absent, null or non-numeric
    /// settlement value makes the whole message invalid, and never a sale of <c>0</c>.
    /// </summary>
    [JsonPropertyName("SeValue")]
    public JsonElement? SettlementValue { get; set; }

    /// <summary>
    /// <c>Transaction Status ID</c>, documented as "Transaction Status ID, indicating transaction
    /// status with a numeric value". It is the only source of a parsed message's status: unlike the
    /// <c>lastSales</c> path, nothing here infers a status from the amount.
    /// </summary>
    [JsonPropertyName("Transaction Status ID")]
    public JsonElement? TransactionStatusId { get; set; }

    /// <summary>
    /// <c>Authorization Time</c>, documented as "Transaction Authorization Date and Time (GMT)" -
    /// so it is read through <c>NayaxGmtTimestamp</c>'s GMT rule (issue #471) and never against the
    /// host's time zone.
    /// </summary>
    [JsonPropertyName("Authorization Time")]
    public JsonElement? AuthorizationTime { get; set; }

    /// <summary>
    /// <c>Machine AuTime</c>, documented as "Machine Authorization Date and Time based on Machine
    /// Time Zone": machine-local wall-clock time carrying no offset, kept as a raw imported fact
    /// exactly as the <c>lastSales</c> field of the same name is, and never read as an instant.
    /// </summary>
    [JsonPropertyName("Machine AuTime")]
    public JsonElement? MachineAuthorizationTime { get; set; }

    /// <summary>
    /// <c>Cancel Type</c>, documented as the cancel reason, where "value 255 means successfully
    /// settled transaction". The published example carries <c>null</c> on a settled transaction, so
    /// absent and <c>255</c> both mean settled.
    /// </summary>
    [JsonPropertyName("Cancel Type")]
    public JsonElement? CancelType { get; set; }

    /// <summary>
    /// <c>Settlment Failed</c> - Nayax's spelling, kept exactly as the contract publishes it -
    /// documented as "Indicates if the settlement process failed".
    /// </summary>
    [JsonPropertyName("Settlment Failed")]
    public JsonElement? SettlementFailed { get; set; }

    /// <summary>
    /// <c>Is Revalue Transaction</c>: a prepaid credit top-up rather than a product sale.
    /// </summary>
    [JsonPropertyName("Is Revalue Transaction")]
    public JsonElement? IsRevalueTransaction { get; set; }

    /// <summary><c>Is Revalue Reward Transaction</c>: the reward form of the same thing.</summary>
    [JsonPropertyName("Is Revalue Reward Transaction")]
    public JsonElement? IsRevalueRewardTransaction { get; set; }

    /// <summary>
    /// <c>Is Multivend</c>, documented as "Bit indicating if transaction is a Multivend (Multi
    /// selection sale/vend)". Multivend is deferred by parent #1100, so such a message is reported
    /// as multivend and left to the hourly <c>lastSales</c> run.
    /// </summary>
    [JsonPropertyName("Is Multivend")]
    public JsonElement? IsMultivend { get; set; }

    /// <summary><c>Currency</c>, documented as the machine's transaction currency.</summary>
    [JsonPropertyName("Currency")]
    public JsonElement? Currency { get; set; }

    /// <summary>
    /// <c>Payment Method Description</c>, documented as "Payment Method Name" - the value
    /// <c>PaymentMethodClassifier</c> classifies as card or cash, exactly as it classifies the
    /// <c>lastSales</c> payment method.
    /// </summary>
    [JsonPropertyName("Payment Method Description")]
    public JsonElement? PaymentMethodDescription { get; set; }

    /// <summary><c>Machine Name</c>, documented as the machine's configured name.</summary>
    [JsonPropertyName("Machine Name")]
    public JsonElement? MachineName { get; set; }

    /// <summary>
    /// <c>Cost Price</c>, documented as "Product Cost Price as defined under Administration >
    /// Products". The reference lists it among the product fields, and the published example message
    /// carries it under <c>Data</c>, so both places are read - the product entry first.
    /// </summary>
    [JsonPropertyName("Cost Price")]
    public JsonElement? CostPrice { get; set; }

    /// <summary>
    /// <c>Products</c>: the nested array of vended items. Read as an element for the reason
    /// <c>Data</c> is - an absent, null, empty or non-array value is a stated invalid outcome - and
    /// its length is also what tells a multivend message apart from a single-selection one.
    /// </summary>
    [JsonPropertyName("Products")]
    public JsonElement? Products { get; set; }
}

/// <summary>
/// The allow-listed fields of one entry of a message's <c>Data.Products</c> array. The entry also
/// carries the PA code, the product group, the catalogue number and the per-item prices; none of
/// them is declared, because nothing in this application reads them.
/// </summary>
public sealed class NayaxSqsTransactionProduct
{
    /// <summary>
    /// <c>Product ID</c>, documented as "Product ID (Nayax Internal Identifier)" and carried by the
    /// first real message inside the product entry (the reference's one flat field table documents
    /// the field but not which layer it arrives at). It is the identifier the application's product
    /// links are keyed by, so a usable value is used as it is; a value that is not a positive
    /// integer is no identifier, and the slot below is then the only thing the message says about
    /// which product was vended besides its name.
    /// </summary>
    [JsonPropertyName("Product ID")]
    public JsonElement? ProductId { get; set; }

    /// <summary><c>Product Name</c>, documented as the product's configured name.</summary>
    [JsonPropertyName("Product Name")]
    public JsonElement? ProductName { get; set; }

    /// <summary>
    /// <c>Product Code in Map</c>, documented as "Product Selection Item Number (MDB Code)" - the
    /// machine's slot. It travels beside every saved report, and the reader resolves it to a Nayax
    /// product id through the machine's product map
    /// (<see cref="NayaxSqsSlotProductResolver"/>) when the message carried no usable
    /// <c>Product ID</c>.
    /// </summary>
    [JsonPropertyName("Product Code in Map")]
    public JsonElement? ProductCodeInMap { get; set; }

    /// <summary>The product entry's own <c>Cost Price</c>, preferred over the one under <c>Data</c>.</summary>
    [JsonPropertyName("Cost Price")]
    public JsonElement? CostPrice { get; set; }
}
