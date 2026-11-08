using System.Globalization;
using Inventory.Application.NayaxSqs;
using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: the parser read against the <strong>shape of the first real message</strong> the
/// owner's queue delivered (<see cref="NayaxSqsRealMessage"/>, field names and structure from
/// 5 November 2026 with invented values), rather than only against the published documentation
/// sample.
///
/// <para>The real message differs from the sample in four ways that decide money and stock: its
/// product entry carries Nayax's own <c>"Product ID"</c>, its <c>Data</c> repeats both root
/// identifiers, its flags and its <c>Cancel Type</c> arrive as <c>null</c> rather than as
/// <c>false</c> or <c>255</c> on a perfectly ordinary settled sale, and its GMT timestamps carry no
/// <c>Z</c> designator while sitting eleven hours behind the machine's Sydney wall clock. Each of
/// those is a way a message could have been refused, saved at the wrong instant, or attached to the
/// wrong product.</para>
/// </summary>
public class NayaxSqsRealMessageTests
{
    /// <summary>The GMT instant the real message's offset-free <c>Authorization Time</c> names.</summary>
    private static readonly DateTime AuthorizationInstantUtc =
        new(2026, 11, 5, 2, 42, 17, DateTimeKind.Utc);

    private const string OffsetFreeAuthorizationTime = "2026-11-05T02:42:17.000";

    private static NayaxSqsTransactionOutcome CompletedSaleOutcomeFrom(string body)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);

        Assert.Equal(NayaxSqsRejectionReason.None, outcome.Reason);
        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        return outcome;
    }

    /// <summary>
    /// The whole mapping from the real message's shape. The product id, the amount, the status, the
    /// instant and the cost price are the five that decide which product was sold, for how much,
    /// on which day, and at what margin.
    /// </summary>
    [Fact]
    public void The_first_real_messages_shape_maps_onto_the_sale_record_field_by_field()
    {
        var outcome = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.Received);

        var report = outcome.Report!;
        Assert.Equal(4100000001L, report.TransactionID);
        Assert.Equal(4200001L, report.MachineID);
        Assert.Equal(12, report.TransactionStatusId);
        Assert.Equal(3.5000m, report.SettlementValue);
        Assert.Equal(AuthorizationInstantUtc, report.AuthorizationInstantUtc);
        Assert.Equal(new DateTime(2026, 11, 5, 13, 42, 17), report.MachineAuthorizationTime);
        Assert.Equal("AUD", report.CurrencyCode);
        Assert.Equal("Credit Card", report.PaymentMethod);
        Assert.Equal("Fictional Depot Machine 1", report.MachineName);
        Assert.Equal("Fictional Spring Water 600ml", report.ProductName);

        // The real message carries Cost Price under Data rather than on the product entry, which is
        // why both places are read.
        Assert.Equal(1.2000m, report.ProductCostPrice);

        // Nayax's own product id, which the published sample's product entry does not carry at all.
        // The slot still travels beside the report: the reader resolves it only when the message
        // gave no usable id.
        Assert.Equal(4300001L, report.NayaxProductId);
        Assert.Equal(14, outcome.ProductSlot);
    }

    /// <summary>
    /// The real message's <c>Cancel Type</c> is <c>null</c> on a settled card vend - not the
    /// documented <c>255</c> - so null may not be read as a cancel reason. If it were, the first
    /// ordinary sale off the queue would have been refused as a status contradiction.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData(null)]
    public void A_settled_sale_whose_cancel_type_is_null_or_absent_is_a_completed_sale(string? cancelType)
    {
        var outcome = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.ReceivedWith(("Cancel Type", cancelType)));

        Assert.Equal(12, outcome.Report!.TransactionStatusId);
        Assert.Equal(3.5000m, outcome.Report.SettlementValue);
    }

    /// <summary>
    /// And <c>255</c>, the documented "successfully settled" value, means the same thing - the two
    /// shapes of one settled sale cannot disagree.
    /// </summary>
    [Fact]
    public void A_settled_sale_whose_cancel_type_is_255_is_also_a_completed_sale()
    {
        var outcome = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.ReceivedWith(("Cancel Type", "255")));

        Assert.Equal(12, outcome.Report!.TransactionStatusId);
    }

    /// <summary>
    /// Every boolean flag the parser reads diverts a message away from being saved, and the real
    /// message proves Nayax sends some of them as <c>null</c> on an ordinary sale. Null or absent
    /// therefore means <strong>false</strong>: only an explicit <c>true</c> triggers a rule, because
    /// reading "not set" as "set" would quietly discard real sales.
    /// </summary>
    [Theory]
    [InlineData("Settlment Failed")]
    [InlineData("Is Revalue Transaction")]
    [InlineData("Is Revalue Reward Transaction")]
    [InlineData("Is Multivend")]
    public void A_null_or_absent_data_flag_is_false(string flag)
    {
        Assert.Equal(
            NayaxSqsOutcomeKind.CompletedSale,
            NayaxSqsTransactionParser.Parse(NayaxSqsRealMessage.ReceivedWith((flag, "null"))).Kind);
        Assert.Equal(
            NayaxSqsOutcomeKind.CompletedSale,
            NayaxSqsTransactionParser.Parse(NayaxSqsRealMessage.ReceivedWith((flag, null))).Kind);
    }

    /// <summary>The same rule for the root <c>Void</c> flag, whose meaning is undocumented.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData(null)]
    public void A_null_or_absent_void_flag_is_false(string? isVoid)
    {
        Assert.Equal(
            NayaxSqsOutcomeKind.CompletedSale,
            NayaxSqsTransactionParser.Parse(NayaxSqsRealMessage.ReceivedWithRoot("Void", isVoid)).Kind);
    }

    /// <summary>
    /// A product id the message states is used as it is: it is Nayax's own identifier for the
    /// product, which is what the stored link is keyed by, so resolving it again from the slot would
    /// be a second guess at something the provider already answered.
    /// </summary>
    [Fact]
    public void The_messages_own_product_id_becomes_the_sales_nayax_product_id()
    {
        var outcome = CompletedSaleOutcomeFrom(
            NayaxSqsRealMessage.ReceivedWithProduct(("Product ID", "4300002")));

        Assert.Equal(4300002L, outcome.Report!.NayaxProductId);
    }

    /// <summary>
    /// A product id that is not a positive integer is no product id. The sale is still a sale and
    /// still carries its slot, so the reader resolves the product through the machine's product map
    /// and, failing that, the existing unambiguous-name fallback in <c>ProductMatcher.MatchLinked</c>
    /// applies exactly as it does today. Zero is the case worth naming: <c>0</c> is a real slot but
    /// never a real Nayax product id, and accepting it would attach the sale to no product while
    /// looking like an answer.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-4300001")]
    [InlineData("null")]
    [InlineData(null)]
    [InlineData("\"4300001\"")]
    [InlineData("4300001.5")]
    [InlineData("{}")]
    public void An_unusable_product_id_leaves_the_product_to_the_slot_resolver(string? productId)
    {
        var outcome = CompletedSaleOutcomeFrom(
            NayaxSqsRealMessage.ReceivedWithProduct(("Product ID", productId)));

        Assert.Null(outcome.Report!.NayaxProductId);
        Assert.Equal(14, outcome.ProductSlot);
        Assert.Equal("Fictional Spring Water 600ml", outcome.Report.ProductName);
    }

    /// <summary>
    /// The real message's GMT timestamps carry no <c>Z</c> designator, so the designator cannot be
    /// what makes them UTC: the field contract says GMT (issue #471). Both renderings name one
    /// instant, and neither is what a Sydney-hosted API binding an offset-free value against its own
    /// zone would have stored - eleven hours early, on the previous Sydney business day.
    /// </summary>
    [Theory]
    [InlineData(11)] // AEDT, which is what Sydney was on 5 November 2026
    [InlineData(10)] // AEST
    public void The_offset_free_authorization_time_is_one_instant_and_never_the_host_offset(
        int hostOffsetHours)
    {
        var offsetFree = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.Received).Report!;
        var withDesignator = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.ReceivedWith(
            ("Authorization Time", $"\"{OffsetFreeAuthorizationTime}Z\""))).Report!;

        var asASydneyHostWouldBind = new DateTimeOffset(
            DateTime.Parse(OffsetFreeAuthorizationTime, CultureInfo.InvariantCulture, DateTimeStyles.None),
            TimeSpan.FromHours(hostOffsetHours)).UtcDateTime;

        Assert.Equal(AuthorizationInstantUtc, offsetFree.AuthorizationInstantUtc);
        Assert.Equal(AuthorizationInstantUtc, withDesignator.AuthorizationInstantUtc);
        Assert.Equal(TimeSpan.Zero, offsetFree.AuthorizationDateTimeGmt!.Value.Offset);
        Assert.NotEqual(asASydneyHostWouldBind, offsetFree.AuthorizationInstantUtc);

        // The machine's own wall clock is eleven hours ahead of the instant in this message and is
        // kept exactly as reported, never converted and never mistaken for the instant.
        Assert.NotEqual(offsetFree.MachineAuthorizationTime, offsetFree.AuthorizationInstantUtc);
    }

    /// <summary>
    /// <c>"Credit Card"</c> is the description the first real message carried, and the one central
    /// <see cref="PaymentMethodClassifier"/> already classifies it as card - so it is reimbursed and
    /// attracts a processing fee, unlike a cash vend. The mapping carries the string through
    /// unchanged, so no SQS-specific string matching is added anywhere.
    /// </summary>
    [Fact]
    public void The_real_messages_payment_description_is_classified_as_card()
    {
        var report = CompletedSaleOutcomeFrom(NayaxSqsRealMessage.Received).Report!;

        Assert.Equal("Credit Card", report.PaymentMethod);
        Assert.Equal(NayaxPaymentType.Card, PaymentMethodClassifier.Classify(report.PaymentMethod));
    }

    /// <summary>
    /// The status is the message's own <c>Transaction Status ID</c> and never its amount. The real
    /// message confirms <c>12</c> is "Settled" for an ordinary card vend; a declined message of the
    /// same shape, charging the same money, stays non-completed and keeps its own status.
    /// </summary>
    [Fact]
    public void A_declined_message_of_the_same_shape_keeps_its_own_status_and_its_product_id()
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsRealMessage.ReceivedWith(
            ("Transaction Status ID", "26"), ("Transaction Status Name", "\"Declined\"")));

        Assert.Equal(NayaxSqsOutcomeKind.CancelledOrDeclined, outcome.Kind);
        Assert.Equal(26, outcome.Report!.TransactionStatusId);
        Assert.Equal(3.5000m, outcome.Report.SettlementValue);
        Assert.Equal(4300001L, outcome.Report.NayaxProductId);
        Assert.Equal(14, outcome.ProductSlot);
    }
}
