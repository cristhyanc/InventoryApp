using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Inventory.Application.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: a Nayax SQS transaction message carries the consumer's card and identity, and this
/// application has no use for any of it. These cases pin the two halves of that rule - the
/// deserialisation allow-list, and that nothing from the payload can leave the parser in an outcome,
/// a reason or an exception.
///
/// <para>The fixture is the published example message, whose card and consumer values are fictional
/// but genuinely present in the document (<see cref="NayaxSqsExampleMessage"/>), so these tests read
/// a real document rather than a subset that could not have leaked anything in the first
/// place.</para>
/// </summary>
public class NayaxSqsTransactionPrivacyTests
{
    /// <summary>
    /// The complete allow-list: every JSON field name the three message models declare, and nothing
    /// else. A new member is a deliberate decision that has to be made here as well, which is the
    /// point - the card, consumer and discount-card fields are absent because nothing reads them.
    /// </summary>
    private static readonly string[] AllowedRootFields =
        ["TransactionId", "MachineId", "Void", "Data"];

    private static readonly string[] AllowedDataFields =
    [
        "SeValue",
        "Transaction Status ID",
        "Authorization Time",
        "Machine AuTime",
        "Cancel Type",
        "Settlment Failed",
        "Is Revalue Transaction",
        "Is Revalue Reward Transaction",
        "Is Multivend",
        "Currency",
        "Payment Method Description",
        "Machine Name",
        "Cost Price",
        "Products",
    ];

    private static readonly string[] AllowedProductFields =
        ["Product Name", "Product Code in Map", "Cost Price"];

    /// <summary>
    /// The card and customer fields the issue names explicitly. None of them may be declared by any
    /// of the three models, whatever else changes.
    /// </summary>
    private static readonly string[] ForbiddenFields =
    [
        "Card String",
        "Card First 4 Digits",
        "Card Last 4 Digits",
        "Card Type",
        "Card BIN",
        "Credit Card Type",
        "Display Card Number",
        "Card Holder Name",
        "Consumer ID",
        "Token",
        "Zip Code",
        "User Password",
        "User Identity",
        "Discount Card ID",
        "Discount Card Number",
        "Discount Card User Identity",
        "Discount Card Physical Type ID",
        "Discount Card Activation Date",
        "Discount Card Expiration Date",
        "Loyalty Card Number",
        "CLI",
        "Guest Name",
    ];

    private static IEnumerable<string> DeclaredJsonFields(Type model) =>
        model
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? property.Name);

    [Theory]
    [InlineData(typeof(NayaxSqsTransactionMessage))]
    [InlineData(typeof(NayaxSqsTransactionData))]
    [InlineData(typeof(NayaxSqsTransactionProduct))]
    public void No_card_or_customer_field_is_declared_by_any_message_model(Type model)
    {
        var declared = DeclaredJsonFields(model).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(declared.Intersect(ForbiddenFields, StringComparer.Ordinal));
    }

    /// <summary>
    /// The allow-list is exactly the mapped and classifying fields. This is an equality rather than
    /// a "does not contain", so a field added without a decision fails here and is noticed.
    /// </summary>
    [Fact]
    public void The_message_models_declare_exactly_the_allow_listed_fields()
    {
        Assert.Equal(
            AllowedRootFields.Order(StringComparer.Ordinal),
            DeclaredJsonFields(typeof(NayaxSqsTransactionMessage)).Order(StringComparer.Ordinal));
        Assert.Equal(
            AllowedDataFields.Order(StringComparer.Ordinal),
            DeclaredJsonFields(typeof(NayaxSqsTransactionData)).Order(StringComparer.Ordinal));
        Assert.Equal(
            AllowedProductFields.Order(StringComparer.Ordinal),
            DeclaredJsonFields(typeof(NayaxSqsTransactionProduct)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every outcome a message can get, from the real document: none of them carries a card or
    /// consumer value, in the outcome's own text or in anything serialized out of it. The invalid
    /// and unsupported cases matter most - a message that was refused is exactly the one whose
    /// content a log would be tempted to quote.
    /// </summary>
    public static TheoryData<string, string> ExampleOutcomes() => new()
    {
        { "a completed sale", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12")) },
        { "a declined transaction", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "26")) },
        { "a refund", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "62")) },
        { "an unknown status", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "99")) },
        { "a revalue", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12"), ("Is Revalue Transaction", "true")) },
        { "a multivend", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12"), ("Is Multivend", "true")) },
        { "a free vend", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12"), ("SeValue", "0")) },
        { "a status contradiction", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12"), ("Cancel Type", "3")) },
        { "a message with no status at all", NayaxSqsExampleMessage.Published },
        { "a message whose amount is text", NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "12"), ("SeValue", "\"1.00\"")) },
        { "a truncated message", NayaxSqsExampleMessage.Published[..400] },
    };

    /// <summary>
    /// A guard on the fixture itself: every value the rule below forbids really is in the document,
    /// so that rule is tested against a payload that genuinely carries one rather than against a
    /// subset that could never have leaked anything.
    /// </summary>
    [Fact]
    public void The_fixture_really_carries_the_card_and_customer_values()
    {
        foreach (var value in NayaxSqsExampleMessage.CardAndCustomerValues)
            Assert.Contains(value, NayaxSqsExampleMessage.Published, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExampleOutcomes))]
    public void No_card_or_customer_value_reaches_an_outcome(string because, string body)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);

        var text = string.Join(
            " ",
            outcome.ToString(),
            outcome.Reason.ToString(),
            JsonSerializer.Serialize(outcome),
            outcome.Report is null ? string.Empty : JsonSerializer.Serialize(outcome.Report));

        foreach (var value in NayaxSqsExampleMessage.CardAndCustomerValues)
            Assert.False(
                text.Contains(value, StringComparison.Ordinal),
                $"The outcome of {because} contains the card or customer value '{value}'.");
    }

    /// <summary>
    /// The parser answers with a reason instead of throwing, for every single field of the real
    /// document arriving as the wrong type. That is what makes the rule above hold for exception
    /// text too: there is no exception, so there is nothing to log a payload fragment from.
    /// </summary>
    [Fact]
    public void No_single_malformed_field_can_make_the_parser_throw()
    {
        var data = JsonNode.Parse(NayaxSqsExampleMessage.Published)!.AsObject()["Data"]!.AsObject();

        foreach (var key in data.Select(field => field.Key).ToList())
        {
            var body = NayaxSqsExampleMessage.PublishedWith(
                ("Transaction Status ID", "12"),
                (key, """{ "unexpected": [1, 2, 3] }"""));

            var outcome = NayaxSqsTransactionParser.Parse(body);

            Assert.True(
                Enum.IsDefined(outcome.Kind),
                $"'{key}' arriving as an object must still produce a stated outcome.");
        }
    }

    /// <summary>
    /// And for a body that is not a transaction message at all, including the shapes an operator
    /// mis-wiring a queue would deliver.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<xml />")]
    [InlineData("{")]
    [InlineData("[{ \"TransactionId\": 1 }]")]
    public void A_body_that_is_not_a_transaction_message_is_invalid_rather_than_an_exception(string? body)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);

        Assert.Equal(NayaxSqsOutcomeKind.Invalid, outcome.Kind);
        Assert.Equal(NayaxSqsRejectionReason.NotJson, outcome.Reason);
    }
}
