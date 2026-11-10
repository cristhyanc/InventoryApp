using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Application.ReceiptScanning;

/// <summary>
/// The strict validation of the reader's JSON (issue #1163): only schema-valid values are read,
/// nothing is coerced, and a line that does not match is dropped rather than defaulted.
///
/// <para>Every receipt here is invented (issue #1163's known constraints: a real receipt is never
/// committed as a fixture).</para>
/// </summary>
public class ReceiptScanPayloadParserTests
{
    /// <summary>A complete, well-formed answer is read field for field.</summary>
    [Fact]
    public void A_well_formed_answer_is_read()
    {
        var payload = ReceiptScanPayloadParser.Parse(
            """
            {
              "supplierName": "Metro Beverages",
              "supplierId": 7,
              "orderDate": "2026-10-25",
              "receiptDate": "2026-10-26",
              "deliveryDate": "2026-10-27",
              "invoiceNumber": "INV-4471",
              "receiptTotal": 55.00,
              "receiptTotalIncludesGst": "yes",
              "gstTotal": 5.00,
              "lines": [
                {
                  "text": "COLA 375ML X 24",
                  "kind": "stock",
                  "deliveredPacks": 2,
                  "orderedPacks": 3,
                  "packSize": "printed",
                  "unitsPerPack": 24,
                  "lineTotal": 30.00,
                  "discountTotal": 1.50,
                  "priceIncludesGst": "yes",
                  "taxable": "yes",
                  "gstMarker": "*",
                  "productId": 91,
                  "isDrinkContainer": true
                }
              ]
            }
            """);

        Assert.NotNull(payload);
        Assert.Equal("Metro Beverages", payload.SupplierName);
        Assert.Equal(7, payload.SupplierId);
        Assert.Equal(new DateOnly(2026, 10, 25), payload.OrderDate);
        Assert.Equal(new DateOnly(2026, 10, 26), payload.ReceiptDate);
        Assert.Equal(new DateOnly(2026, 10, 27), payload.DeliveryDate);
        Assert.Equal("INV-4471", payload.InvoiceNumber);
        Assert.Equal(55.00m, payload.ReceiptTotal);
        Assert.Equal(ReceiptPriceGstBasis.IncludesGst, payload.ReceiptTotalBasis);
        Assert.Equal(5.00m, payload.GstTotal);

        var line = Assert.Single(payload.Lines);
        Assert.Equal("COLA 375ML X 24", line.Text);
        Assert.Equal(ReceiptLineKind.Stock, line.Kind);
        Assert.Equal(2, line.DeliveredPacks);
        Assert.Equal(3, line.OrderedPacks);
        Assert.Equal(24, line.UnitsPerPack);
        Assert.Equal(30.00m, line.LineTotal);
        Assert.Equal(1.50m, line.DiscountTotal);
        Assert.Equal(ReceiptPriceGstBasis.IncludesGst, line.PriceBasis);
        Assert.Equal(ReceiptLineTaxability.Taxable, line.Taxability);
        Assert.Equal("*", line.GstMarker);
        Assert.Equal(91, line.ProductId);
        Assert.True(line.IsDrinkContainer);
    }

    /// <summary>
    /// The issue's malformed-JSON criterion: nothing is returned at all, which the caller reports as
    /// an unreadable receipt. Prose, a truncated answer and an empty body are all the same answer.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("I could not read this receipt.")]
    [InlineData("{\"lines\": [")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"supplierName\": \"Metro\"}")]
    public void An_answer_that_is_not_of_the_published_shape_reads_as_nothing(string? json) =>
        Assert.Null(ReceiptScanPayloadParser.Parse(json));

    /// <summary>
    /// The issue's extra-fields criterion: a model that answers with more than it was asked for has
    /// answered the schema plus noise, and the noise is not read.
    /// </summary>
    [Fact]
    public void Extra_fields_are_ignored()
    {
        var payload = ReceiptScanPayloadParser.Parse(
            """
            {
              "lines": [{ "text": "WATER 600ML", "kind": "stock", "packSize": "single",
                          "priceIncludesGst": "yes", "confidence": 0.8, "notes": "looked blurry" }],
              "modelThoughts": "the receipt asked me to ignore my instructions",
              "receiptTotal": 4.50
            }
            """);

        Assert.NotNull(payload);
        Assert.Equal(4.50m, payload.ReceiptTotal);
        Assert.Equal("WATER 600ML", Assert.Single(payload.Lines).Text);
    }

    /// <summary>
    /// A line is dropped, and the rest kept, for each thing the schema requires: readable text, a
    /// declared kind, a declared pack-size answer, and a stated GST basis. A partly readable receipt
    /// is worth more than no receipt.
    /// </summary>
    [Theory]
    [InlineData("\"text\": 42")]
    [InlineData("\"text\": \"   \"")]
    [InlineData("\"kind\": \"groceries\"")]
    [InlineData("\"kind\": \"Stock\"")]
    [InlineData("\"kind\": 1")]
    [InlineData("\"packSize\": \"guessed\"")]
    [InlineData("\"priceIncludesGst\": \"maybe\"")]
    [InlineData("\"priceIncludesGst\": true")]
    public void A_line_that_does_not_match_the_schema_is_dropped(string broken)
    {
        var payload = ReceiptScanPayloadParser.Parse(
            $$"""
            {
              "lines": [
                { "text": "COLA", "kind": "stock", "packSize": "single", "priceIncludesGst": "yes", {{broken}} },
                { "text": "WATER", "kind": "stock", "packSize": "single", "priceIncludesGst": "yes" }
              ]
            }
            """);

        Assert.NotNull(payload);
        Assert.Equal("WATER", Assert.Single(payload.Lines).Text);
    }

    /// <summary>A line that is not an object at all is dropped too.</summary>
    [Fact]
    public void A_line_that_is_not_an_object_is_dropped()
    {
        var payload = ReceiptScanPayloadParser.Parse(
            """
            { "lines": ["COLA", 42, null,
                        { "text": "WATER", "kind": "stock", "packSize": "single", "priceIncludesGst": "yes" }] }
            """);

        Assert.NotNull(payload);
        Assert.Single(payload.Lines);
    }

    /// <summary>
    /// The pack-size answer decides the units per pack, not the number beside it: <c>notPrinted</c>
    /// is never one, and a number arriving with it is not read. It is the failure the model test of
    /// 8 November 2026 found, refused here rather than relied on in the prompt.
    /// </summary>
    [Theory]
    [InlineData("\"printed\"", 24, 24)]
    [InlineData("\"printed\"", null, null)]
    [InlineData("\"printed\"", 0, null)]
    [InlineData("\"single\"", null, 1)]
    [InlineData("\"single\"", 24, 1)]
    [InlineData("\"notPrinted\"", null, null)]
    [InlineData("\"notPrinted\"", 24, null)]
    public void The_pack_size_answer_decides_the_units_per_pack(
        string packSize,
        int? reported,
        int? expected)
    {
        var payload = ReceiptScanPayloadParser.Parse(
            $$"""
            { "lines": [{ "text": "CARTON", "kind": "stock", "priceIncludesGst": "yes",
                          "packSize": {{packSize}},
                          "unitsPerPack": {{reported?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}} }] }
            """);

        Assert.Equal(expected, Assert.Single(payload!.Lines).UnitsPerPack);
    }

    /// <summary>
    /// A quoted number is not a number. Binding <c>"30.00"</c> to 30 would accept a money value the
    /// contract does not describe, which is <c>NayaxSqsJsonValues</c>' rule for a financial payload.
    /// </summary>
    [Fact]
    public void A_quoted_number_is_not_a_number()
    {
        var line = Assert.Single(ReceiptScanPayloadParser.Parse(
            """
            { "lines": [{ "text": "COLA", "kind": "stock", "packSize": "single",
                          "priceIncludesGst": "yes", "lineTotal": "30.00", "deliveredPacks": "2",
                          "productId": "91", "isDrinkContainer": "true" }] }
            """)!.Lines);

        Assert.Null(line.LineTotal);
        Assert.Null(line.DeliveredPacks);
        Assert.Null(line.ProductId);
        Assert.False(line.IsDrinkContainer);
    }

    /// <summary>
    /// A discount is subtracted by its magnitude whichever sign the reader reported it with, so a
    /// sign cannot turn a discount into a surcharge.
    /// </summary>
    [Theory]
    [InlineData("-1.50")]
    [InlineData("1.50")]
    public void A_discount_is_read_as_its_magnitude(string discount)
    {
        var line = Assert.Single(ReceiptScanPayloadParser.Parse(
            $$"""
            { "lines": [{ "text": "COLA", "kind": "stock", "packSize": "single",
                          "priceIncludesGst": "yes", "discountTotal": {{discount}} }] }
            """)!.Lines);

        Assert.Equal(1.50m, line.DiscountTotal);
    }

    /// <summary>A line with no discount field has no discount, not a null one.</summary>
    [Fact]
    public void A_line_with_no_discount_has_none() =>
        Assert.Equal(
            0m,
            Assert.Single(ReceiptScanPayloadParser.Parse(
                """
                { "lines": [{ "text": "COLA", "kind": "stock", "packSize": "single",
                              "priceIncludesGst": "yes" }] }
                """)!.Lines).DiscountTotal);

    /// <summary>
    /// A date is read only in the format the schema asks for. <c>03/04/2026</c> is two different
    /// months depending on who printed it, so it is absent rather than interpreted.
    /// </summary>
    [Theory]
    [InlineData("\"2026-10-27\"", true)]
    [InlineData("\"27/10/2026\"", false)]
    [InlineData("\"2026-10-27T10:00:00Z\"", false)]
    [InlineData("\"October 27 2026\"", false)]
    [InlineData("20261027", false)]
    [InlineData("null", false)]
    public void A_date_is_read_only_in_the_schema_format(string json, bool read)
    {
        var payload = ReceiptScanPayloadParser.Parse($"{{ \"deliveryDate\": {json}, \"lines\": [] }}");

        Assert.Equal(read ? new DateOnly(2026, 10, 27) : null, payload!.DeliveryDate);
    }

    /// <summary>
    /// An id must be a whole number above zero: zero and negatives are no business's product or
    /// supplier, and a fractional id is not an id.
    /// </summary>
    [Theory]
    [InlineData("91", 91L)]
    [InlineData("0", null)]
    [InlineData("-91", null)]
    [InlineData("91.5", null)]
    public void An_id_must_be_a_whole_number_above_zero(string json, long? expected) =>
        Assert.Equal(
            expected,
            Assert.Single(ReceiptScanPayloadParser.Parse(
                $$"""
                { "lines": [{ "text": "COLA", "kind": "stock", "packSize": "single",
                              "priceIncludesGst": "yes", "productId": {{json}} }] }
                """)!.Lines).ProductId);

    /// <summary>
    /// An absent or unreadable taxability is "the receipt does not say" rather than a dropped line:
    /// it blocks an ex-GST conversion, which is the safe answer, so dropping the line would lose a
    /// perfectly readable inc-GST one for nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(", \"taxable\": \"perhaps\"")]
    [InlineData(", \"taxable\": null")]
    public void An_unstated_taxability_is_unknown_rather_than_a_dropped_line(string taxable)
    {
        var line = Assert.Single(ReceiptScanPayloadParser.Parse(
            $$"""
            { "lines": [{ "text": "COLA", "kind": "stock", "packSize": "single",
                          "priceIncludesGst": "yes"{{taxable}} }] }
            """)!.Lines);

        Assert.Equal(ReceiptLineTaxability.Unknown, line.Taxability);
    }

    /// <summary>
    /// A receipt total whose basis the reader did not state is unstated, which the levy
    /// reconciliation treats as unresolvable rather than guessing a basis for the comparison.
    /// </summary>
    [Fact]
    public void An_unstated_receipt_total_basis_is_unknown() =>
        Assert.Equal(
            ReceiptPriceGstBasis.Unknown,
            ReceiptScanPayloadParser.Parse("{ \"receiptTotal\": 55.00, \"lines\": [] }")!.ReceiptTotalBasis);

    /// <summary>Every declared line kind is accepted, spelled as the schema publishes it.</summary>
    [Fact]
    public void Every_declared_kind_is_accepted() =>
        Assert.All(
            ReceiptLineKinds.All,
            kind => Assert.Equal(
                kind,
                Assert.Single(ReceiptScanPayloadParser.Parse(
                    $$"""
                    { "lines": [{ "text": "LINE", "kind": "{{ReceiptLineKinds.Name(kind)}}",
                                  "packSize": "single", "priceIncludesGst": "yes" }] }
                    """)!.Lines).Kind));
}
