using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Turns one raw Nayax SQS transaction message body into exactly one
/// <see cref="NayaxSqsTransactionOutcome"/>: either the sale record this application already knows
/// how to save, or an explicit "do not save" with a reason (issue #1102).
///
/// <para><strong>Every "what does this message mean" rule lives here</strong>, so the queue reader
/// only moves messages. The outcome is chosen by the first rule below that applies, and the order is
/// the rule - a revalue that is also a multivend is a revalue, and a free vend whose cancel type
/// contradicts its status is a free vend:</para>
/// <list type="number">
///   <item><strong>Invalid</strong> - not JSON, or missing a field a sale cannot be built from:
///   <c>TransactionId</c>, <c>MachineId</c>, <c>Data</c>, <c>Data.SeValue</c>,
///   <c>Data."Transaction Status ID"</c>, <c>Data."Authorization Time"</c> or a non-empty
///   <c>Data.Products</c>; or carrying a <c>Data</c> copy of either identifier
///   (<c>Data."Transaction ID"</c>, <c>Data."Machine ID"</c>) that disagrees with the root
///   value.</item>
///   <item><strong>Revalue</strong> - a prepaid credit top-up, not a product sale.</item>
///   <item><strong>Multivend</strong> - by the flag or by more than one product; deferred by parent
///   #1100 to the hourly <c>lastSales</c> run.</item>
///   <item><strong>Unsupported</strong> - <c>Void</c>, a failed settlement, a refund, a pending
///   status, an unrecognised status, or a negative amount.</item>
///   <item><strong>CancelledOrDeclined</strong> - status <c>26</c>, <c>28</c>, <c>31</c> or
///   <c>250</c>, saved as a non-completed row so it stays visible in status reporting.</item>
///   <item><strong>FreeVend</strong> - approved and charging exactly nothing; unsupported until a
///   real sample exists.</item>
///   <item><strong>CompletedSale</strong> - approved, charging money, settled.</item>
///   <item><strong>Unsupported</strong> - approved but contradicted by a cancel reason.</item>
/// </list>
///
/// <para><strong>The status is the message's own <c>Transaction Status ID</c> and is never inferred
/// from the amount</strong>, which is what the <c>lastSales</c> path does today (its store reads a
/// positive settlement value as completed and anything else as cancelled). Nayax publishes no list
/// of status id values - neither the Transaction JSON reference nor the delivery setup page states
/// one, checked on 5 November 2026 - so the ids are the application's own established ones
/// (<see cref="NayaxTransactionStatusIds"/>, AGENTS.md § Sales, payment methods, and statuses),
/// classified by the one <see cref="NayaxTransactionStatusClassifier"/> every import and report
/// already uses. Any other value is unsupported, so an unexpected status can never become a
/// completed sale, and parent #1100's rollout step confirms the real values before production
/// consumption is switched on.</para>
///
/// <para><strong>Nothing here reads <c>Is Refund Card</c></strong>, which marks a Nayax Refund Card
/// rather than a refunded transaction. A refund is recognised by status <c>62</c> and by nothing
/// else; reading the card flag as refund evidence would quietly stop counting real sales.</para>
///
/// <para><strong>Every boolean flag is false unless the message says <c>true</c></strong>. The
/// first real message sends <c>Settlment Failed</c> and <c>Is Revalue Reward Transaction</c> as
/// <c>null</c> on an ordinary settled sale, and each flag the parser reads diverts a message away
/// from being saved, so reading "not set" as "set" would discard real sales. <c>Cancel Type</c>
/// follows the same rule: absent and <c>null</c> both mean "no cancel reason", which is what that
/// message carries on a transaction it also calls settled.</para>
///
/// <para><strong>Nothing here makes a call</strong> - no AWS SDK, no EF Core, no Nayax read. The
/// report's <c>NayaxProductId</c> is the product entry's own <c>Product ID</c> when the message
/// states a usable one, and the message's slot is returned beside the report either way, for the
/// reader to resolve through <see cref="INayaxSqsSlotProductResolver"/> when it is not.</para>
/// </summary>
public static class NayaxSqsTransactionParser
{
    /// <summary>
    /// The <c>Cancel Type</c> value documented as "successfully settled transaction". Absent and
    /// <c>null</c> mean the same thing - the published example message carries <c>null</c> on a
    /// settled transaction - and any other value contradicts an approved status.
    /// </summary>
    private const int SettledCancelType = 255;

    /// <summary>
    /// Reads one message body and answers what it means. It never throws for a payload-shaped
    /// reason: a body that is not JSON, a field of the wrong type and a value out of range are all
    /// stated outcomes with a reason code, because the reader has to decide what to do with a
    /// message it cannot save, and a message it cannot read is exactly the one whose content must
    /// not be echoed into an exception or a log.
    /// </summary>
    public static NayaxSqsTransactionOutcome Parse(string? messageBody)
    {
        if (!TryDeserialize(messageBody, out var message))
            return NayaxSqsTransactionOutcome.Invalid(NayaxSqsRejectionReason.NotJson);

        // Rule 1, in the order the issue states it. Each of these is a field a sale cannot be built
        // without, so a message missing one is a permanent failure rather than something to retry.
        if (!TryReadRequiredFields(message, out var fields, out var incompleteReason))
            return NayaxSqsTransactionOutcome.Invalid(incompleteReason);

        // Rule 2: a prepaid credit top-up (or its reward form) is not a product sale at all, and
        // that is true however the rest of the message reads.
        if (NayaxSqsJsonValues.IsTrue(fields.Data.IsRevalueTransaction)
            || NayaxSqsJsonValues.IsTrue(fields.Data.IsRevalueRewardTransaction))
            return NayaxSqsTransactionOutcome.Revalue();

        // Rule 3: multivend, by the flag or by the array holding more than one item. The existing
        // store keeps one product per transaction and does not guarantee multivend quantities, so
        // parent #1100 leaves these to the hourly lastSales run rather than saving one of the items.
        if (NayaxSqsJsonValues.IsTrue(fields.Data.IsMultivend) || fields.Products.GetArrayLength() > 1)
            return NayaxSqsTransactionOutcome.Multivend();

        // Rule 4: the transactions this application deliberately does not consume from the queue.
        if (NayaxSqsJsonValues.IsTrue(message.IsVoid))
            return NayaxSqsTransactionOutcome.Unsupported(NayaxSqsRejectionReason.VoidTransaction);

        if (NayaxSqsJsonValues.IsTrue(fields.Data.SettlementFailed))
            return NayaxSqsTransactionOutcome.Unsupported(NayaxSqsRejectionReason.SettlementFailed);

        return ClassifyApprovableOutcome(fields);
    }

    /// <summary>Rule 0: a body that is not JSON, or whose root is not the documented object.</summary>
    private static bool TryDeserialize(
        string? messageBody, [NotNullWhen(true)] out NayaxSqsTransactionMessage? message)
    {
        message = null;

        if (string.IsNullOrWhiteSpace(messageBody))
            return false;

        try
        {
            message = JsonSerializer.Deserialize<NayaxSqsTransactionMessage>(messageBody);
        }
        catch (JsonException)
        {
            // Deliberately not rethrown and deliberately not logged here: the exception's message
            // carries the position inside the body, and the body is a consumer's card transaction.
            return false;
        }

        return message is not null;
    }

    /// <summary>
    /// The fields rule 1 requires. Holding them together lets the rest of <see cref="Parse"/> read
    /// as the ordered rule table the issue describes, instead of a wall of local variables.
    /// </summary>
    private readonly record struct RequiredFields(
        long TransactionId,
        long MachineId,
        NayaxSqsTransactionData Data,
        decimal SettlementValue,
        int TransactionStatusId,
        DateTimeOffset AuthorizedAtGmt,
        JsonElement Products);

    private static bool TryReadRequiredFields(
        NayaxSqsTransactionMessage message, out RequiredFields fields, out NayaxSqsRejectionReason reason)
    {
        fields = default;

        if (NayaxSqsJsonValues.AsInt64(message.TransactionId) is not { } transactionId)
        {
            reason = NayaxSqsRejectionReason.TransactionIdUnreadable;
            return false;
        }

        if (NayaxSqsJsonValues.AsInt64(message.MachineId) is not { } machineId)
        {
            reason = NayaxSqsRejectionReason.MachineIdUnreadable;
            return false;
        }

        if (NayaxSqsJsonValues.AsObject(message.Data) is not { } dataElement)
        {
            reason = NayaxSqsRejectionReason.DataMissing;
            return false;
        }

        var data = JsonSerializer.Deserialize<NayaxSqsTransactionData>(dataElement)!;

        // The amount is the one value that may never be defaulted. A missing, null or non-numeric
        // SeValue makes the message invalid; it never becomes a sale of 0, which the amount-derived
        // lastSales rule would then import as cancelled.
        if (NayaxSqsJsonValues.AsDecimal(data.SettlementValue) is not { } settlementValue)
        {
            reason = NayaxSqsRejectionReason.SettlementValueUnreadable;
            return false;
        }

        if (NayaxSqsJsonValues.AsInt32(data.TransactionStatusId) is not { } transactionStatusId)
        {
            reason = NayaxSqsRejectionReason.TransactionStatusIdUnreadable;
            return false;
        }

        // Read through the one GMT rule (issue #471): the field is documented as GMT, so an
        // offset-free value is UTC because of that contract and never because of the host's time
        // zone. A value that is absent or is not a documented date-time shape has no instant at all.
        if (NayaxGmtTimestamp.Parse(NayaxSqsJsonValues.AsString(data.AuthorizationTime))
            is not { } authorizedAtGmt)
        {
            reason = NayaxSqsRejectionReason.AuthorizationTimeUnreadable;
            return false;
        }

        if (NayaxSqsJsonValues.AsNonEmptyArray(data.Products) is not { } products)
        {
            reason = NayaxSqsRejectionReason.ProductsMissing;
            return false;
        }

        // The last clause of rule 1. Data repeats both root identifiers, and the two copies of one
        // identifier may not disagree: a message like that is not a message about one transaction,
        // and saving it would attribute a sale to whichever copy this code happened to read.
        // Absent and null copies say nothing and are no contradiction; a copy that is present but
        // unreadable cannot be shown to agree, so it is refused - the fail-closed reading.
        if (!NayaxSqsJsonValues.IsAbsent(data.TransactionId)
            && NayaxSqsJsonValues.AsInt64(data.TransactionId) != transactionId)
        {
            reason = NayaxSqsRejectionReason.TransactionIdMismatch;
            return false;
        }

        if (!NayaxSqsJsonValues.IsAbsent(data.MachineId)
            && NayaxSqsJsonValues.AsInt64(data.MachineId) != machineId)
        {
            reason = NayaxSqsRejectionReason.MachineIdMismatch;
            return false;
        }

        fields = new RequiredFields(
            transactionId, machineId, data, settlementValue, transactionStatusId, authorizedAtGmt, products);
        reason = NayaxSqsRejectionReason.None;
        return true;
    }

    /// <summary>
    /// Rules 4 through 8: what an approved or cancelled/declined status (rule 4 has already refused
    /// every other classification) ends up meaning once the message is known to carry a sale.
    /// </summary>
    private static NayaxSqsTransactionOutcome ClassifyApprovableOutcome(RequiredFields fields)
    {
        var status = NayaxTransactionStatusClassifier.Classify(fields.TransactionStatusId);
        var statusReason = status switch
        {
            NayaxTransactionStatus.Refunded => NayaxSqsRejectionReason.Refunded,
            NayaxTransactionStatus.Pending => NayaxSqsRejectionReason.PendingStatus,
            NayaxTransactionStatus.Unknown => NayaxSqsRejectionReason.UnrecognisedStatus,
            _ => NayaxSqsRejectionReason.None
        };
        if (statusReason != NayaxSqsRejectionReason.None)
            return NayaxSqsTransactionOutcome.Unsupported(statusReason);

        if (fields.SettlementValue < 0)
            return NayaxSqsTransactionOutcome.Unsupported(NayaxSqsRejectionReason.NegativeSettlementValue);

        var product = NayaxSqsJsonValues.FirstProduct(fields.Products);
        var productSlot = NayaxSqsJsonValues.AsInt32(product?.ProductCodeInMap);
        var report = MapReport(
            fields.TransactionId,
            fields.MachineId,
            fields.TransactionStatusId,
            fields.SettlementValue,
            fields.AuthorizedAtGmt,
            fields.Data,
            product);

        // Rule 5: cancelled and declined rows are saved too, carrying the status the message
        // reported, so they stay visible in status reporting instead of being discarded.
        if (status == NayaxTransactionStatus.CancelledOrDeclined)
            return NayaxSqsTransactionOutcome.CancelledOrDeclined(report, productSlot);

        // Only the approved status reaches here: rule 4 refused every other classification, so
        // there is no fourth branch below that could quietly accept one.

        // Rule 6: approved and charging exactly nothing. Neither the reference nor the delivery
        // setup page says how a free vend is marked, so it waits for a real sample.
        if (fields.SettlementValue == 0)
            return NayaxSqsTransactionOutcome.FreeVend();

        // Rules 7 and 8: a cancel reason other than "settled successfully" contradicts an approved
        // status, and neither of the two is believed over the other. A present but unreadable value
        // counts as a contradiction rather than as an absent one, which is the fail-closed answer.
        var cancelTypeAbsent = NayaxSqsJsonValues.IsAbsent(fields.Data.CancelType);
        var cancelType = NayaxSqsJsonValues.AsInt32(fields.Data.CancelType);

        return cancelTypeAbsent || cancelType == SettledCancelType
            ? NayaxSqsTransactionOutcome.CompletedSale(report, productSlot)
            : NayaxSqsTransactionOutcome.Unsupported(NayaxSqsRejectionReason.StatusContradiction);
    }

    /// <summary>
    /// The mapping onto the record the application already saves. Only the allow-listed fields are
    /// read; <c>NayaxProductId</c> comes from the product entry's own <c>Product ID</c> when that
    /// is a usable identifier, and is otherwise left unset for the reader to resolve from the slot.
    /// </summary>
    private static NayaxLastSalesReport MapReport(
        long transactionId,
        long machineId,
        int transactionStatusId,
        decimal settlementValue,
        DateTimeOffset authorizedAtGmt,
        NayaxSqsTransactionData data,
        NayaxSqsTransactionProduct? product) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId,

            // Nayax's own product id, when the message states a usable one. It is an external
            // identity and is carried as reported: the product links the application stores are
            // keyed by this value, so resolving the slot again instead would be a second guess at
            // something the provider already answered. A value that is not a positive integer is
            // no identifier at all - 0 is a real slot but never a real product id - and leaves the
            // field unset for the reader's slot lookup and the existing name fallback.
            NayaxProductId = AsProductIdentifier(product?.ProductId),

            // The supplied status, which the store honours as given from issue #1103 onwards. It is
            // the message's own Transaction Status ID and nothing derived from the amount.
            TransactionStatusId = transactionStatusId,

            SettlementValue = settlementValue,
            AuthorizationDateTimeGmt = authorizedAtGmt,

            // Machine-local wall-clock time, kept as a raw imported fact exactly as the lastSales
            // field of the same name is, and never read as an instant. A message carrying no
            // readable value leaves it at its default rather than borrowing the GMT instant, which
            // would make a machine's local time look like something Nayax reported.
            MachineAuthorizationTime =
                NayaxSqsJsonValues.AsDateTime(data.MachineAuthorizationTime) ?? default,

            CurrencyCode = NayaxSqsJsonValues.AsString(data.Currency),
            PaymentMethod = NayaxSqsJsonValues.AsString(data.PaymentMethodDescription),
            MachineName = NayaxSqsJsonValues.AsString(data.MachineName),
            ProductName = NayaxSqsJsonValues.AsString(product?.ProductName),

            // The reference lists Cost Price among the product fields while the published example
            // message carries it under Data, so both are read and the item's own value wins. Absent
            // in both places it stays unknown rather than becoming zero.
            ProductCostPrice =
                NayaxSqsJsonValues.AsDecimal(product?.CostPrice)
                ?? NayaxSqsJsonValues.AsDecimal(data.CostPrice)
        };

    /// <summary>
    /// The field as a Nayax product id, which is a positive integer or nothing. Zero, a negative
    /// value and every non-integer shape are reported as no id rather than carried, so a sale is
    /// never attached to a product id that cannot exist.
    /// </summary>
    private static long? AsProductIdentifier(JsonElement? element) =>
        NayaxSqsJsonValues.AsInt64(element) is { } productId && productId > 0 ? productId : null;
}
