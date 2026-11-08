using Inventory.Application.NayaxSqs;
using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: an SQS message's payment method is classified card or cash by the one central
/// <see cref="PaymentMethodClassifier"/>, exactly as a <c>lastSales</c> value for the same payment
/// type is (AGENTS.md: "Use <c>PaymentMethodClassifier</c> as the central card/cash/unknown
/// classification. Do not add report-specific string matching").
///
/// <para>These cases also record <em>why no translation table exists</em>. The SQS field is
/// <c>Payment Method Description</c>, documented as "Payment Method Name" - the same meaning as the
/// <c>lastSales</c> payment method - and the strings the Transaction JSON reference actually shows
/// are already the ones the classifier recognises. Nothing is rewritten at the boundary because
/// nothing has to be; if a real message turns out to carry a description the classifier does not
/// know (parent #1100's rollout step records the real strings), the translation belongs here, at the
/// mapping, and never as another report's own string matching.</para>
/// </summary>
public class NayaxSqsPaymentMethodTests
{
    private static readonly (string Key, string Json) Approved = ("Transaction Status ID", "12");

    private static string? MappedPaymentMethod(string paymentMethodDescriptionJson)
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsExampleMessage.PublishedWith(
            Approved, ("Payment Method Description", paymentMethodDescriptionJson)));

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        return outcome.Report!.PaymentMethod;
    }

    /// <summary>
    /// The documented example's own description. <c>Prepaid Credit</c> is a Nayax prepaid card
    /// payment, which the classifier already treats as card - so it is reimbursed and attracts a
    /// processing fee, unlike a cash vend.
    /// </summary>
    [Fact]
    public void The_documented_examples_payment_description_is_classified_as_card()
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsExampleMessage.PublishedWith(Approved));

        Assert.Equal("Prepaid Credit", outcome.Report!.PaymentMethod);
        Assert.Equal(NayaxPaymentType.Card, PaymentMethodClassifier.Classify(outcome.Report.PaymentMethod));
    }

    /// <summary>
    /// The payment descriptions the Transaction JSON reference and the application's own established
    /// values cover, each classified by the one classifier rather than by anything added here.
    /// </summary>
    [Theory]
    [InlineData("Prepaid Credit", NayaxPaymentType.Card)]
    [InlineData("Credit Card", NayaxPaymentType.Card)]
    [InlineData("Cash", NayaxPaymentType.Cash)]
    public void A_mapped_payment_description_is_classified_by_the_central_classifier(
        string paymentMethodDescription, NayaxPaymentType expected)
    {
        var mapped = MappedPaymentMethod($"\"{paymentMethodDescription}\"");

        // The string is carried through unchanged: the classifier is the one place the card/cash
        // decision is made, so the mapping may not pre-decide it.
        Assert.Equal(paymentMethodDescription, mapped);
        Assert.Equal(expected, PaymentMethodClassifier.Classify(mapped));
    }

    /// <summary>
    /// The description comes from <c>Payment Method Description</c> and from nowhere else. The
    /// published example also carries <c>Recognition Description</c> and
    /// <c>Payment Service (Mobile using Credit Card)</c>, both of which say "Prepaid Credit" there,
    /// so a cash message is the case that proves which field was read.
    /// </summary>
    [Fact]
    public void The_payment_method_comes_from_the_payment_method_description_field()
    {
        var mapped = MappedPaymentMethod("\"Cash\"");

        Assert.Equal("Cash", mapped);
        Assert.Equal(NayaxPaymentType.Cash, PaymentMethodClassifier.Classify(mapped));
    }

    /// <summary>
    /// A description the classifier does not recognise stays unknown, and the sale is still a sale.
    /// An unknown payment type is a visible data-quality state the reports already carry; guessing
    /// card or cash here would move money between the reimbursed and non-reimbursed columns.
    /// </summary>
    [Theory]
    [InlineData("\"Something Nayax Invented\"")]
    [InlineData("null")]
    public void An_unrecognised_or_missing_payment_description_stays_unknown(string paymentMethodDescriptionJson)
    {
        var mapped = MappedPaymentMethod(paymentMethodDescriptionJson);

        Assert.Equal(NayaxPaymentType.Unknown, PaymentMethodClassifier.Classify(mapped));
    }
}
