using System.Globalization;
using Inventory.Application.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: the ordered outcome table of <see cref="NayaxSqsTransactionParser"/>. One message
/// body gets exactly one outcome, chosen by the first rule that applies, and these cases pin both
/// the rules and their order - a revalue that is also a multivend is a revalue, and an approved
/// status contradicted by a cancel reason is believed as neither.
///
/// <para>The rules are financial: this table is what decides whether a queued Nayax message becomes
/// a completed sale, a visible non-completed row, or nothing at all. Nayax publishes no list of
/// <c>Transaction Status ID</c> values, so the parser reuses the application's established ids
/// (AGENTS.md § Sales, payment methods, and statuses) and refuses every other value - which is why
/// <see cref="An_unknown_status_id_can_never_become_a_completed_sale"/> is here.</para>
/// </summary>
public class NayaxSqsTransactionParserOutcomeTests
{
    /// <summary>A readable GMT authorization time, as the JSON string token it arrives as.</summary>
    private const string AuthorizationTime = "\"2020-04-20T07:34:05.063\"";

    /// <summary>One vended item in the machine's slot 0, the shape of a single-selection sale.</summary>
    private const string OneProduct = """[{ "Product Name": "Bounty", "Product Code in Map": 0 }]""";

    private const string TwoProducts =
        """[{ "Product Name": "Bounty", "Product Code in Map": 0 }, { "Product Name": "Mars", "Product Code in Map": 1 }]""";

    /// <summary>
    /// A message whose every field is the completed-sale case, with each field overridable as the
    /// raw JSON token it arrives as. <see langword="null"/> leaves the key out of the document
    /// altogether, which is how "missing" is told apart from an explicit JSON <c>null</c>.
    /// </summary>
    private static string Message(
        string? transactionId = "3804536984",
        string? machineId = "54265",
        string? dataTransactionId = "3804536984",
        string? dataMachineId = "54265",
        string? isVoid = "false",
        string? transactionStatusId = "12",
        string? settlementValue = "1.0000",
        string? authorizationTime = AuthorizationTime,
        string? cancelType = "null",
        string? settlmentFailed = "null",
        string? isRevalueTransaction = "false",
        string? isRevalueRewardTransaction = "false",
        string? isMultivend = "false",
        string? isRefundCard = null,
        string? products = OneProduct) =>
        JsonObject(
            ("TransactionId", transactionId),
            ("MachineId", machineId),
            ("Void", isVoid),
            ("Data", JsonObject(
                // The Data object repeats both root identifiers, as the first real message does.
                ("Transaction ID", dataTransactionId),
                ("Machine ID", dataMachineId),
                ("SeValue", settlementValue),
                ("Transaction Status ID", transactionStatusId),
                ("Authorization Time", authorizationTime),
                ("Cancel Type", cancelType),
                ("Settlment Failed", settlmentFailed),
                ("Is Revalue Transaction", isRevalueTransaction),
                ("Is Revalue Reward Transaction", isRevalueRewardTransaction),
                ("Is Multivend", isMultivend),
                ("Is Refund Card", isRefundCard),
                ("Products", products))));

    /// <summary>A JSON object of the fields whose raw token is not <see langword="null"/>.</summary>
    private static string JsonObject(params (string Key, string? Value)[] fields) =>
        "{"
        + string.Join(
            ",",
            fields.Where(field => field.Value is not null).Select(field => $"\"{field.Key}\": {field.Value}"))
        + "}";

    /// <summary>
    /// Rule 1 of the table: a body that is not a readable transaction at all. Each case is invalid
    /// with its own reason code, and none of them is "a sale of zero" or "a sale at midnight".
    /// </summary>
    public static TheoryData<string, string, NayaxSqsRejectionReason> InvalidMessages() => new()
    {
        { "not JSON at all", "this is not JSON", NayaxSqsRejectionReason.NotJson },
        { "an empty body", "", NayaxSqsRejectionReason.NotJson },
        { "a blank body", "   ", NayaxSqsRejectionReason.NotJson },
        { "a truncated document", """{ "TransactionId": 1 """, NayaxSqsRejectionReason.NotJson },
        { "the JSON null literal", "null", NayaxSqsRejectionReason.NotJson },
        { "a JSON array", "[]", NayaxSqsRejectionReason.NotJson },
        { "a bare JSON number", "12", NayaxSqsRejectionReason.NotJson },

        { "no TransactionId", Message(transactionId: null), NayaxSqsRejectionReason.TransactionIdUnreadable },
        { "a null TransactionId", Message(transactionId: "null"), NayaxSqsRejectionReason.TransactionIdUnreadable },
        { "a TransactionId sent as text", Message(transactionId: "\"3804536984\""), NayaxSqsRejectionReason.TransactionIdUnreadable },
        { "a fractional TransactionId", Message(transactionId: "3804536984.5"), NayaxSqsRejectionReason.TransactionIdUnreadable },

        { "no MachineId", Message(machineId: null), NayaxSqsRejectionReason.MachineIdUnreadable },
        { "a null MachineId", Message(machineId: "null"), NayaxSqsRejectionReason.MachineIdUnreadable },
        { "a MachineId sent as text", Message(machineId: "\"54265\""), NayaxSqsRejectionReason.MachineIdUnreadable },

        { "no Data", """{ "TransactionId": 1, "MachineId": 2 }""", NayaxSqsRejectionReason.DataMissing },
        { "a null Data", """{ "TransactionId": 1, "MachineId": 2, "Data": null }""", NayaxSqsRejectionReason.DataMissing },
        { "a Data that is not an object", """{ "TransactionId": 1, "MachineId": 2, "Data": "detail" }""", NayaxSqsRejectionReason.DataMissing },

        { "no SeValue", Message(settlementValue: null), NayaxSqsRejectionReason.SettlementValueUnreadable },
        { "a null SeValue", Message(settlementValue: "null"), NayaxSqsRejectionReason.SettlementValueUnreadable },
        { "a SeValue sent as text", Message(settlementValue: "\"1.00\""), NayaxSqsRejectionReason.SettlementValueUnreadable },
        { "a SeValue of NaN", Message(settlementValue: "\"NaN\""), NayaxSqsRejectionReason.SettlementValueUnreadable },
        { "a SeValue that is an object", Message(settlementValue: "{}"), NayaxSqsRejectionReason.SettlementValueUnreadable },

        { "no Transaction Status ID", Message(transactionStatusId: null), NayaxSqsRejectionReason.TransactionStatusIdUnreadable },
        { "a null Transaction Status ID", Message(transactionStatusId: "null"), NayaxSqsRejectionReason.TransactionStatusIdUnreadable },
        { "a Transaction Status ID sent as text", Message(transactionStatusId: "\"12\""), NayaxSqsRejectionReason.TransactionStatusIdUnreadable },
        { "a fractional Transaction Status ID", Message(transactionStatusId: "12.5"), NayaxSqsRejectionReason.TransactionStatusIdUnreadable },

        { "no Authorization Time", Message(authorizationTime: null), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },
        { "a null Authorization Time", Message(authorizationTime: "null"), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },
        { "a blank Authorization Time", Message(authorizationTime: "\"\""), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },
        { "a date-only Authorization Time", Message(authorizationTime: "\"2020-04-20\""), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },
        { "an ambiguous Authorization Time", Message(authorizationTime: "\"20/04/2020 07:34:05\""), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },
        { "an Authorization Time sent as a number", Message(authorizationTime: "1587367445"), NayaxSqsRejectionReason.AuthorizationTimeUnreadable },

        { "no Products", Message(products: null), NayaxSqsRejectionReason.ProductsMissing },
        { "a null Products", Message(products: "null"), NayaxSqsRejectionReason.ProductsMissing },
        { "an empty Products", Message(products: "[]"), NayaxSqsRejectionReason.ProductsMissing },
        { "a Products that is not an array", Message(products: """{ "Product Name": "Bounty" }"""), NayaxSqsRejectionReason.ProductsMissing },

        // The last clause of rule 1: the Data object repeats both root identifiers, and a message
        // whose two copies disagree is not a message about one transaction. A present but
        // unreadable copy is a disagreement too - the fail-closed reading - because an identifier
        // that does not round-trip cannot be shown to be the same one.
        { "a Data Transaction ID that disagrees with the root", Message(dataTransactionId: "3804536985"), NayaxSqsRejectionReason.TransactionIdMismatch },
        { "a Data Transaction ID sent as text", Message(dataTransactionId: "\"3804536984\""), NayaxSqsRejectionReason.TransactionIdMismatch },
        { "a Data Machine ID that disagrees with the root", Message(dataMachineId: "54266"), NayaxSqsRejectionReason.MachineIdMismatch },
        { "a Data Machine ID sent as text", Message(dataMachineId: "\"54265\""), NayaxSqsRejectionReason.MachineIdMismatch },
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Rule_1_a_message_that_cannot_be_read_is_invalid_with_its_own_reason(
        string because, string body, NayaxSqsRejectionReason expectedReason)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);

        Assert.True(
            outcome.Kind == NayaxSqsOutcomeKind.Invalid && outcome.Reason == expectedReason,
            $"A message with {because} must be Invalid ({expectedReason}), "
                + $"but was {outcome.Kind} ({outcome.Reason}).");
        Assert.Null(outcome.Report);
        Assert.Null(outcome.ProductSlot);
    }

    /// <summary>
    /// A malformed amount is the case the issue singles out, because the <c>lastSales</c> path
    /// derives a status from the amount: a message whose amount cannot be read must never arrive as
    /// a zero-value transaction, which that path would then import as cancelled.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"1.00\"")]
    [InlineData("\"NaN\"")]
    public void An_unreadable_amount_is_invalid_and_never_becomes_zero(string? settlementValue)
    {
        var outcome = NayaxSqsTransactionParser.Parse(Message(settlementValue: settlementValue));

        Assert.Equal(NayaxSqsOutcomeKind.Invalid, outcome.Kind);
        Assert.Equal(NayaxSqsRejectionReason.SettlementValueUnreadable, outcome.Reason);
        Assert.Null(outcome.Report);
    }

    /// <summary>Rules 2 to 8: each row of the table, and the order between the rows.</summary>
    public static TheoryData<string, string, NayaxSqsOutcomeKind, NayaxSqsRejectionReason> ClassifiedMessages() => new()
    {
        // Rule 2: revalue, before every rule below it.
        { "a revalue transaction", Message(isRevalueTransaction: "true"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },
        { "a revalue reward transaction", Message(isRevalueRewardTransaction: "true"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },
        { "a revalue that is also a multivend", Message(isRevalueTransaction: "true", isMultivend: "true"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },
        { "a revalue that is also void", Message(isRevalueTransaction: "true", isVoid: "true"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },
        { "a revalue with an unknown status id", Message(isRevalueTransaction: "true", transactionStatusId: "99"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },

        // Rule 3: multivend, by the flag or by the array.
        { "a multivend by its flag", Message(isMultivend: "true"), NayaxSqsOutcomeKind.Multivend, NayaxSqsRejectionReason.None },
        { "a multivend by two products", Message(products: TwoProducts), NayaxSqsOutcomeKind.Multivend, NayaxSqsRejectionReason.None },
        { "a multivend that is also void", Message(isMultivend: "true", isVoid: "true"), NayaxSqsOutcomeKind.Multivend, NayaxSqsRejectionReason.None },
        { "a multivend that is also refunded", Message(isMultivend: "true", transactionStatusId: "62"), NayaxSqsOutcomeKind.Multivend, NayaxSqsRejectionReason.None },

        // Rule 4: unsupported.
        { "a void transaction", Message(isVoid: "true"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.VoidTransaction },
        { "a failed settlement", Message(settlmentFailed: "true"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.SettlementFailed },
        { "a transaction pending settlement", Message(transactionStatusId: "55"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.PendingStatus },
        { "a transaction pending batch", Message(transactionStatusId: "80"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.PendingStatus },
        { "an unknown status id", Message(transactionStatusId: "99"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.UnrecognisedStatus },
        { "a zero status id", Message(transactionStatusId: "0"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.UnrecognisedStatus },
        { "a negative amount", Message(settlementValue: "-1.0000"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.NegativeSettlementValue },
        { "a negative amount on a declined transaction", Message(transactionStatusId: "26", settlementValue: "-1.0000"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.NegativeSettlementValue },
        { "a negative amount on a refund", Message(transactionStatusId: "62", settlementValue: "-1.0000"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.NegativeSettlementValue },

        // Rule 4a: the refund of issue #1107, after the negative-amount refusal and before the
        // cancelled/declined branch. It is never saved as a sale, so a cancel reason that would
        // contradict an approved status says nothing about it.
        { "a refunded transaction", Message(transactionStatusId: "62"), NayaxSqsOutcomeKind.Refund, NayaxSqsRejectionReason.None },
        { "a refund with a cancel reason", Message(transactionStatusId: "62", cancelType: "3"), NayaxSqsOutcomeKind.Refund, NayaxSqsRejectionReason.None },
        { "a refund charging nothing", Message(transactionStatusId: "62", settlementValue: "0"), NayaxSqsOutcomeKind.Refund, NayaxSqsRejectionReason.None },
        { "a void refund", Message(transactionStatusId: "62", isVoid: "true"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.VoidTransaction },
        { "a revalue that is also refunded", Message(transactionStatusId: "62", isRevalueTransaction: "true"), NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None },

        // Rule 5: cancelled or declined, including when a cancel reason is also present.
        { "status 26", Message(transactionStatusId: "26"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },
        { "status 28", Message(transactionStatusId: "28"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },
        { "status 31", Message(transactionStatusId: "31"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },
        { "status 250", Message(transactionStatusId: "250"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },
        { "a declined transaction with a cancel reason", Message(transactionStatusId: "26", cancelType: "3"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },
        { "a declined transaction charging nothing", Message(transactionStatusId: "26", settlementValue: "0"), NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None },

        // Rule 6: free vend, ahead of the contradiction rule below.
        { "an approved transaction charging nothing", Message(settlementValue: "0"), NayaxSqsOutcomeKind.FreeVend, NayaxSqsRejectionReason.None },
        { "an approved transaction charging 0.0000", Message(settlementValue: "0.0000"), NayaxSqsOutcomeKind.FreeVend, NayaxSqsRejectionReason.None },
        { "a free vend with a cancel reason", Message(settlementValue: "0", cancelType: "3"), NayaxSqsOutcomeKind.FreeVend, NayaxSqsRejectionReason.None },

        // Rule 7: the completed sale.
        { "an approved sale with no cancel type", Message(cancelType: null), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },
        { "an approved sale with a null cancel type", Message(cancelType: "null"), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },
        { "an approved sale settled successfully", Message(cancelType: "255"), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },
        { "an approved sale on a Nayax refund card", Message(isRefundCard: "true"), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },
        { "an approved sale carrying no Data copy of its identifiers", Message(dataTransactionId: null, dataMachineId: null), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },
        { "an approved sale whose Data copies of its identifiers are null", Message(dataTransactionId: "null", dataMachineId: "null"), NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None },

        // Rule 8: the contradiction.
        { "an approved sale cancelled for another reason", Message(cancelType: "3"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.StatusContradiction },
        { "an approved sale whose cancel type is 0", Message(cancelType: "0"), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.StatusContradiction },
        { "an approved sale whose cancel type is unreadable", Message(cancelType: "\"255\""), NayaxSqsOutcomeKind.Unsupported, NayaxSqsRejectionReason.StatusContradiction },
    };

    [Theory]
    [MemberData(nameof(ClassifiedMessages))]
    public void Rules_2_to_8_classify_a_readable_message_by_the_first_rule_that_applies(
        string because,
        string body,
        NayaxSqsOutcomeKind expectedKind,
        NayaxSqsRejectionReason expectedReason)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);

        Assert.True(
            outcome.Kind == expectedKind && outcome.Reason == expectedReason,
            $"A message with {because} must be {expectedKind} ({expectedReason}), "
                + $"but was {outcome.Kind} ({outcome.Reason}).");
    }

    /// <summary>
    /// Only the two saved kinds and the refund carry a report, and every other kind carries none -
    /// so no consumer can save a revalue, a multivend or a rejected message by reading a report that
    /// happened to be there. A refund's report is not a sale to save either: it is the three values
    /// its comparison with the stored sale is made on (issue #1107), and the
    /// <see cref="NayaxSqsOutcomeKind.Refund"/> kind is what keeps the two apart.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassifiedMessages))]
    public void Only_a_saved_outcome_or_a_refund_carries_a_report(
        string because,
        string body,
        NayaxSqsOutcomeKind expectedKind,
        NayaxSqsRejectionReason expectedReason)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);
        var carriesReport = expectedKind
            is NayaxSqsOutcomeKind.CompletedSale
            or NayaxSqsOutcomeKind.CancelledOrDeclined
            or NayaxSqsOutcomeKind.Refund;

        Assert.True(
            carriesReport == (outcome.Report is not null),
            $"A message with {because} is {expectedKind} ({expectedReason}), so it must "
                + $"{(carriesReport ? "carry" : "carry no")} report.");
    }

    /// <summary>
    /// And only the two <em>saved</em> kinds carry a product slot: a refund resolves no product, so a
    /// consumer cannot read a slot off one and ask the machine's product map about it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassifiedMessages))]
    public void Only_a_saved_outcome_carries_a_product_slot(
        string because,
        string body,
        NayaxSqsOutcomeKind expectedKind,
        NayaxSqsRejectionReason expectedReason)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);
        var carriesSlot = expectedKind
            is NayaxSqsOutcomeKind.CompletedSale
            or NayaxSqsOutcomeKind.CancelledOrDeclined;

        Assert.True(
            carriesSlot == (outcome.ProductSlot is not null),
            $"A message with {because} is {expectedKind} ({expectedReason}), so it must "
                + $"{(carriesSlot ? "carry" : "carry no")} product slot.");
    }

    /// <summary>
    /// The status on a saved report is the message's own <c>Transaction Status ID</c>, never
    /// something derived from the amount. A declined transaction that charged money is the case
    /// that tells the two apart: the <c>lastSales</c> rule calls a positive amount completed.
    /// </summary>
    [Fact]
    public void A_declined_message_with_a_positive_amount_is_declined_and_keeps_its_status()
    {
        var outcome = NayaxSqsTransactionParser.Parse(
            Message(transactionStatusId: "26", settlementValue: "4.5000"));

        Assert.Equal(NayaxSqsOutcomeKind.CancelledOrDeclined, outcome.Kind);
        Assert.Equal(26, outcome.Report!.TransactionStatusId);
        Assert.Equal(4.5000m, outcome.Report.SettlementValue);
    }

    /// <summary>
    /// Nayax publishes no list of status ids, so an unexpected one is refused. This is the case that
    /// must never regress into a sale: a value nobody has seen before cannot be approved.
    /// </summary>
    [Theory]
    [InlineData(99)]
    [InlineData(13)]
    [InlineData(1)]
    [InlineData(-12)]
    [InlineData(int.MaxValue)]
    public void An_unknown_status_id_can_never_become_a_completed_sale(int transactionStatusId)
    {
        var outcome = NayaxSqsTransactionParser.Parse(
            Message(transactionStatusId: transactionStatusId.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(NayaxSqsOutcomeKind.Unsupported, outcome.Kind);
        Assert.Equal(NayaxSqsRejectionReason.UnrecognisedStatus, outcome.Reason);
        Assert.Null(outcome.Report);
    }

    /// <summary>
    /// <c>Is Refund Card</c> marks a Nayax Refund Card, not a refunded transaction, so it is not
    /// read for classification at all: an otherwise completed sale that used one is a completed
    /// sale. Reading it as refund evidence would silently stop counting real sales.
    /// </summary>
    [Fact]
    public void A_Nayax_refund_card_is_not_refund_evidence()
    {
        var outcome = NayaxSqsTransactionParser.Parse(Message(isRefundCard: "true"));

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        Assert.Equal(12, outcome.Report!.TransactionStatusId);
    }

    /// <summary>
    /// A refunded transaction is recognised by its status id (<c>62</c>) and nothing else, and is its
    /// own outcome since issue #1107. It carries the report - the three values that say which
    /// transaction it is about - and <strong>no product slot</strong>: a refund resolves no product,
    /// so there is nothing for the machine's product map to be asked.
    /// </summary>
    [Fact]
    public void A_refund_is_recognised_by_its_status_and_carries_a_report_with_no_slot()
    {
        var outcome = NayaxSqsTransactionParser.Parse(
            NayaxSqsRealMessage.ReceivedWith(("Transaction Status ID", "62")));

        Assert.Equal(NayaxSqsOutcomeKind.Refund, outcome.Kind);
        Assert.Equal(NayaxSqsRejectionReason.None, outcome.Reason);

        // No slot: a refund resolves no product, so it carries nothing the machine's product map
        // could be asked about.
        Assert.Null(outcome.ProductSlot);

        // And the three values the comparison is made on, exactly as reported.
        Assert.Equal(62, outcome.Report!.TransactionStatusId);
        Assert.Equal(4100000001L, outcome.Report.TransactionID);
        Assert.Equal(4200001L, outcome.Report.MachineID);
        Assert.Equal(3.5000m, outcome.Report.SettlementValue);
        Assert.Equal(4300001L, outcome.Report.NayaxProductId);
    }

    /// <summary>
    /// <c>Original Transaction ID</c> is never read (AGENTS.md § Nayax contract verification): the
    /// documentation does not say it is populated on a refund, and a refund delivered as a new
    /// transaction pointing back at the original is explicitly a different design. A message carrying
    /// one is classified exactly as it would be without it, and the transaction the refund is about
    /// stays the message's own <c>TransactionId</c>.
    /// </summary>
    [Fact]
    public void A_refund_carrying_an_original_transaction_id_is_classified_without_reading_it()
    {
        var outcome = NayaxSqsTransactionParser.Parse(
            NayaxSqsRealMessage.ReceivedWith(
                ("Transaction Status ID", "62"), ("Original Transaction ID", "4100000000")));

        Assert.Equal(NayaxSqsOutcomeKind.Refund, outcome.Kind);
        Assert.Equal(4100000001L, outcome.Report!.TransactionID);
    }
}
