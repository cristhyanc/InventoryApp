using System.Globalization;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1102: how a readable Nayax SQS message maps onto the <see cref="NayaxLastSalesReport"/>
/// the application already knows how to save, and onto the machine slot that travels beside it.
///
/// <para>The fixture is the published example message (<see cref="NayaxSqsExampleMessage"/>) with
/// the status column an operator selects in Nayax Core set on it, so these cases read the real
/// document - nested <c>Data.Products</c>, keys with spaces, a hundred fields this application has
/// no use for - rather than a hand-made subset that would agree with the parser by
/// construction.</para>
/// </summary>
public class NayaxSqsTransactionParserMappingTests
{
    /// <summary>The approved status an operator selects in Nayax Core; the example predates it.</summary>
    private static readonly (string Key, string Json) Approved = ("Transaction Status ID", "12");

    /// <summary>
    /// The example's own <c>Authorization Time</c>, offset-free exactly as the document prints it,
    /// and the instant its GMT field contract names.
    /// </summary>
    private const string OffsetFreeAuthorizationTime = "2020-04-20T07:34:05.063";

    private static readonly DateTime AuthorizationInstantUtc =
        new(2020, 4, 20, 7, 34, 5, 63, DateTimeKind.Utc);

    private static NayaxLastSalesReport CompletedSaleFrom(string body)
    {
        var outcome = NayaxSqsTransactionParser.Parse(body);
        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        return outcome.Report!;
    }

    /// <summary>
    /// The whole mapping, field by field, from the published example message. The amount, the
    /// status, the instant and the product name are the four that decide money, stock and which day
    /// a sale lands on.
    /// </summary>
    [Fact]
    public void The_published_example_maps_onto_the_sale_record_field_by_field()
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsExampleMessage.PublishedWith(Approved));

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        Assert.Equal(NayaxSqsRejectionReason.None, outcome.Reason);

        var report = outcome.Report!;
        Assert.Equal(3804536984L, report.TransactionID);
        Assert.Equal(54265L, report.MachineID);
        Assert.Equal(12, report.TransactionStatusId);
        Assert.Equal(AuthorizationInstantUtc, report.AuthorizationInstantUtc);
        Assert.Equal(1.0000m, report.SettlementValue);
        Assert.Equal("Bounty", report.ProductName);
        Assert.Equal("EUR", report.CurrencyCode);
        Assert.Equal("Prepaid Credit", report.PaymentMethod);
        Assert.Equal("Oleg's Simulator 1", report.MachineName);

        // The example carries no cost price in either place it may appear, so it stays unknown
        // rather than becoming zero.
        Assert.Null(report.ProductCostPrice);

        // The published example's product entry carries no "Product ID", so the slot is the only
        // thing it says about which product was vended besides its name, and the reader resolves
        // that through the machine's product map. The real message's own id is covered by
        // NayaxSqsRealMessageTests.
        Assert.Equal(0, outcome.ProductSlot);
        Assert.Null(report.NayaxProductId);
    }

    /// <summary>
    /// <c>Machine AuTime</c> is machine-local wall-clock time with no offset, kept as a raw imported
    /// fact exactly as the <c>lastSales</c> field of the same name is. The example's value is three
    /// hours ahead of the GMT instant above, which is what makes the two distinguishable here.
    /// </summary>
    [Fact]
    public void The_machine_wall_clock_time_is_carried_raw_and_is_not_the_instant()
    {
        var report = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(Approved));

        Assert.Equal(new DateTime(2020, 4, 20, 10, 34, 5, 63), report.MachineAuthorizationTime);
        Assert.NotEqual(report.MachineAuthorizationTime, report.AuthorizationInstantUtc);
    }

    /// <summary>
    /// The instant comes from the GMT field contract (issue #471), so the designator cannot change
    /// it: the example's offset-free value and the same value with a <c>Z</c> name one instant.
    /// </summary>
    [Fact]
    public void The_authorization_time_is_the_same_instant_with_and_without_a_Z_designator()
    {
        var offsetFree = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(
            Approved, ("Authorization Time", $"\"{OffsetFreeAuthorizationTime}\"")));
        var withDesignator = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(
            Approved, ("Authorization Time", $"\"{OffsetFreeAuthorizationTime}Z\"")));

        Assert.Equal(AuthorizationInstantUtc, offsetFree.AuthorizationInstantUtc);
        Assert.Equal(AuthorizationInstantUtc, withDesignator.AuthorizationInstantUtc);
        Assert.Equal(DateTimeKind.Utc, offsetFree.AuthorizationInstantUtc!.Value.Kind);
        Assert.Equal(TimeSpan.Zero, offsetFree.AuthorizationDateTimeGmt!.Value.Offset);
    }

    /// <summary>
    /// And the host never decides it. The API is hosted in Sydney, where .NET's own binding of an
    /// offset-free value would have read it ten or eleven hours early - onto the previous Sydney
    /// business day (issue #471). This test simulates that offset rather than installing it:
    /// <see cref="TimeZoneInfo.Local"/> is process-global state the parallel test collections would
    /// race on.
    /// </summary>
    [Theory]
    [InlineData(11)] // AEDT
    [InlineData(10)] // AEST
    public void An_offset_free_authorization_time_is_never_read_against_the_host_offset(int hostOffsetHours)
    {
        var report = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(Approved));

        var asASydneyHostWouldBind = new DateTimeOffset(
            DateTime.Parse(OffsetFreeAuthorizationTime, CultureInfo.InvariantCulture, DateTimeStyles.None),
            TimeSpan.FromHours(hostOffsetHours)).UtcDateTime;

        Assert.Equal(AuthorizationInstantUtc, report.AuthorizationInstantUtc);
        Assert.NotEqual(asASydneyHostWouldBind, report.AuthorizationInstantUtc);
    }

    /// <summary>
    /// The cost price is the product entry's own when it has one. The reference lists
    /// <c>Cost Price</c> among the product fields while the published example carries it under
    /// <c>Data</c>, so both are read - and the item's own value wins.
    /// </summary>
    [Fact]
    public void The_products_own_cost_price_is_preferred()
    {
        var report = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(
            Approved,
            ("Cost Price", "0.8000"),
            ("Products", """[{ "Product Name": "Bounty", "Product Code in Map": 0, "Cost Price": 1.2500 }]""")));

        Assert.Equal(1.2500m, report.ProductCostPrice);
    }

    /// <summary>And the one under <c>Data</c> is the fallback, which is where the example has it.</summary>
    [Fact]
    public void The_transaction_level_cost_price_is_the_fallback()
    {
        var report = CompletedSaleFrom(NayaxSqsExampleMessage.PublishedWith(
            Approved, ("Cost Price", "0.8000")));

        Assert.Equal(0.8000m, report.ProductCostPrice);
    }

    /// <summary>
    /// A message carrying none of the optional columns still maps: each missing value is
    /// <see langword="null"/> rather than a default, so nothing downstream reads a blank currency,
    /// an invented payment method or a cost of zero as a fact.
    /// </summary>
    [Fact]
    public void A_message_without_the_optional_columns_maps_them_to_null()
    {
        var outcome = NayaxSqsTransactionParser.Parse("""
            {
              "TransactionId": 3804536984,
              "MachineId": 54265,
              "Data": {
                "SeValue": 1.0000,
                "Transaction Status ID": 12,
                "Authorization Time": "2020-04-20T07:34:05.063",
                "Products": [{ "Product Name": "Bounty" }]
              }
            }
            """);

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        var report = outcome.Report!;
        Assert.Null(report.CurrencyCode);
        Assert.Null(report.PaymentMethod);
        Assert.Null(report.MachineName);
        Assert.Null(report.ProductCostPrice);
        Assert.Null(report.NayaxProductId);
        Assert.Null(outcome.ProductSlot);
        Assert.Equal("Bounty", report.ProductName);
        Assert.Equal(AuthorizationInstantUtc, report.AuthorizationInstantUtc);
    }

    /// <summary>
    /// A slot that is present but not an integer is no slot at all. The reader then resolves no
    /// product id and the existing unambiguous-name fallback applies, exactly as it does for a
    /// <c>lastSales</c> item whose product id resolves to nothing.
    /// </summary>
    [Theory]
    [InlineData("\"A1\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    public void An_unreadable_slot_is_no_slot(string productCodeInMap)
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsExampleMessage.PublishedWith(
            Approved,
            ("Products", $$"""[{ "Product Name": "Bounty", "Product Code in Map": {{productCodeInMap}} }]""")));

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        Assert.Null(outcome.ProductSlot);
        Assert.Equal("Bounty", outcome.Report!.ProductName);
    }

    /// <summary>
    /// A product entry that is not an object carries none of its three fields, and the message is
    /// still a sale: rule 1 asks only that <c>Products</c> hold an item.
    /// </summary>
    [Fact]
    public void A_product_entry_that_is_not_an_object_carries_no_product_fields()
    {
        var outcome = NayaxSqsTransactionParser.Parse(NayaxSqsExampleMessage.PublishedWith(
            Approved, ("Products", """["Bounty(0,A1)"]""")));

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        Assert.Null(outcome.Report!.ProductName);
        Assert.Null(outcome.ProductSlot);
    }

    /// <summary>
    /// Unknown extra keys are ignored, at the root, inside <c>Data</c> and inside a product entry.
    /// Nayax Core's Transactions Report tab is a column picker, so a message gains fields whenever
    /// an operator selects one, and that must not stop sales arriving.
    /// </summary>
    [Fact]
    public void Unknown_extra_keys_are_ignored()
    {
        var outcome = NayaxSqsTransactionParser.Parse("""
            {
              "TransactionId": 3804536984,
              "MachineId": 54265,
              "A Column Nayax Added Later": { "nested": [1, 2, 3] },
              "Data": {
                "SeValue": 1.0000,
                "Transaction Status ID": 12,
                "Transaction Status Name": "Approved",
                "Authorization Time": "2020-04-20T07:34:05.063",
                "Another New Column": "something",
                "Products": [{ "Product Name": "Bounty", "Product Code in Map": 0, "Product New Column": 7 }]
              }
            }
            """);

        Assert.Equal(NayaxSqsOutcomeKind.CompletedSale, outcome.Kind);
        Assert.Equal("Bounty", outcome.Report!.ProductName);
        Assert.Equal(0, outcome.ProductSlot);
    }

    /// <summary>
    /// A cancelled or declined message maps exactly as a completed one does - it is saved too, as a
    /// non-completed row carrying its own status, so it stays visible in status reporting.
    /// </summary>
    [Fact]
    public void A_declined_message_maps_the_same_way_and_keeps_its_own_status()
    {
        var outcome = NayaxSqsTransactionParser.Parse(
            NayaxSqsExampleMessage.PublishedWith(("Transaction Status ID", "31")));

        Assert.Equal(NayaxSqsOutcomeKind.CancelledOrDeclined, outcome.Kind);
        var report = outcome.Report!;
        Assert.Equal(31, report.TransactionStatusId);
        Assert.Equal(3804536984L, report.TransactionID);
        Assert.Equal(54265L, report.MachineID);
        Assert.Equal(1.0000m, report.SettlementValue);
        Assert.Equal("Bounty", report.ProductName);
        Assert.Equal(AuthorizationInstantUtc, report.AuthorizationInstantUtc);
        Assert.Equal(0, outcome.ProductSlot);
    }
}
