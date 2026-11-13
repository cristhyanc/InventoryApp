using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Inventory.Application.Imports;
using Inventory.Infrastructure.Imports;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// The parsing decisions imported Nayax reimbursement data depends on, pinned on
/// <see cref="ReimbursementXmlParser"/> itself - invariant-culture numbers, UTC-assumed dates,
/// numeric booleans, unparsable values staying unknown rather than zero, every attribute kept as
/// raw JSON, a bare <c>row</c> fragment still importing - and the two shapes it refuses outright.
///
/// <para>These rules used to be pinned through
/// <c>FileSystemPendingReimbursementXmlSourceTests</c>, which read them back through the server
/// folder's adapter. Issue #1237 retired that folder import, so they are asserted here, against the
/// one parser the remaining uploaded-ZIP route uses (issue #1149). Nothing about the rules changed:
/// each test below is the folder adapter's own case, re-aimed at the parser.</para>
///
/// <para>The parser throws rather than deciding what a failure means; the uploaded-archive reader
/// translates that into one entry's own failure, which
/// <see cref="ZipReimbursementArchiveReaderTests"/> owns.</para>
/// </summary>
public class ReimbursementXmlParserTests
{
    private const string FullRow = """
        <root>
          <row report_type="reimbursement" reimbursement_start_date="2026-08-01" reimbursement_end_date="2026-08-31"
               reimbursement_payout_date="2026-09-05T03:04:05" company_name="Vend Co" customer_id="C-1"
               is_reimbursement="1" is_invoice="0" email="a@b.test" active_devices="3" total_devices="4"
               invoice_payment_method="Bank" invoice_payment_method_id="7" total="1234.56" support_email="s@b.test"
               erp_id="E-1" distributor_actor_id="D-1" finance_entity_id="F-1">
            <device entity_id="M-1" machine_type="VM" vertical_type_name="Snacks" vertical_product_type_name="Chips"
                    hw_serial="HW-1" machine_number="12" actor_code="AC-1" location="Lobby"
                    total_billable_trans_count="10" total_billable_trans_amount="100.50"
                    total_not_billable_trans_count="2" total_not_billable_trans_amount="5.25"
                    service_fee="1.10" processing_fee="2.20" has_service_per_transaction="1"
                    total_extra_charge="0.50" net_amount="96.95" />
            <devicePayments entity_id="M-1" payment_method_descr="Credit Card" recognition_descr="Nayax"
                            sales_count="8" total_sum="90.40" processing_fees="1.80" service_fees="0.90" />
            <fees fees_type_id="3" fee_type_descr="Processing" is_service="0" total_sum="10.00"
                  total_sum_with_vat="11.00" vat_percentage="10" avg_fee_amount="0.25" total_count="40"
                  is_previous_period="0" />
            <paymentMethods payment_method_id="1" payment_method_descr="Visa" is_nayax_reimbursement="1"
                            billing_provider="Nayax" total_sales_count="8" total_sales_sum="90.40"
                            recognition_descr="Nayax" is_previous_period="0" />
          </row>
        </root>
        """;

    private static PendingReimbursementXmlFile Parse(string xml, string fileName = "august.xml") =>
        ReimbursementXmlParser.Parse(fileName, Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void A_valid_file_is_parsed_into_its_reimbursement_device_payment_and_fee_facts()
    {
        var file = Parse(FullRow);

        Assert.Equal("august.xml", file.FileName);
        var reimbursement = Assert.Single(file.Reimbursements);
        Assert.Equal("reimbursement", reimbursement.ReportType);
        Assert.Equal("Vend Co", reimbursement.CompanyName);
        Assert.Equal(1234.56m, reimbursement.Total);
        Assert.Equal(3, reimbursement.ActiveDevices);
        Assert.True(reimbursement.IsReimbursement);
        Assert.False(reimbursement.IsInvoice);

        var device = Assert.Single(reimbursement.Devices);
        Assert.Equal("HW-1", device.HardwareSerial);
        Assert.Equal(100.50m, device.TotalBillableTransactionAmount);
        Assert.Equal(2.20m, device.ProcessingFee);
        Assert.True(device.HasServicePerTransaction);
        Assert.Equal(96.95m, device.NetAmount);

        var payment = Assert.Single(reimbursement.DevicePayments);
        Assert.Equal("Credit Card", payment.PaymentMethodDescription);
        Assert.Equal(90.40m, payment.TotalSum);

        // The fee excluding GST, the GST percentage and the fee including GST stay distinct.
        var fee = Assert.Single(reimbursement.Fees);
        Assert.Equal(10.00m, fee.TotalSum);
        Assert.Equal(11.00m, fee.TotalSumWithVat);
        Assert.Equal(10m, fee.VatPercentage);
        Assert.False(fee.IsService);

        var paymentMethod = Assert.Single(reimbursement.PaymentMethods);
        Assert.Equal("Visa", paymentMethod.PaymentMethodDescription);
        Assert.True(paymentMethod.IsNayaxReimbursement);
    }

    /// <summary>
    /// Dates arrive without an offset and are the report's own instants, so they are assumed to
    /// be UTC. A server-local reading would shift every reimbursement period by the host's
    /// offset and silently move sales into the wrong coverage window. The kind assertion is what
    /// makes this test independent of the host's own time zone: a <see cref="DateTimeKind.Unspecified"/>
    /// result is exactly what dropping the UTC assumption produces, and it reads as local time
    /// everywhere downstream.
    /// </summary>
    [Fact]
    public void Dates_without_an_offset_are_read_as_UTC()
    {
        var reimbursement = Assert.Single(Parse(FullRow).Reimbursements);

        foreach (var date in new[]
        {
            reimbursement.ReimbursementStartDate, reimbursement.ReimbursementEndDate, reimbursement.ReimbursementPayoutDate,
        })
        {
            Assert.NotEqual(DateTimeKind.Unspecified, date!.Value.Kind);
        }

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), reimbursement.ReimbursementStartDate!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), reimbursement.ReimbursementEndDate!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 9, 5, 3, 4, 5, DateTimeKind.Utc), reimbursement.ReimbursementPayoutDate!.Value.ToUniversalTime());
    }

    /// <summary>
    /// Numbers are read with the invariant culture: "1234.56" is one thousand two hundred, never
    /// 123456 under a comma-decimal culture.
    /// </summary>
    [Fact]
    public void Numbers_are_read_with_the_invariant_culture()
    {
        var reimbursement = Assert.Single(
            Parse("""<row total="1234.56" active_devices="3" />""").Reimbursements);

        Assert.Equal(1234.56m, reimbursement.Total);
        Assert.Equal(3, reimbursement.ActiveDevices);
    }

    /// <summary>
    /// An unparsable or absent amount stays unknown. Reading it as zero would turn missing
    /// imported data into a reported financial value.
    /// </summary>
    [Fact]
    public void An_unparsable_or_absent_number_or_date_stays_unknown()
    {
        var reimbursement = Assert.Single(
            Parse("""<row total="n/a" active_devices="" reimbursement_start_date="not-a-date" />""")
                .Reimbursements);

        Assert.Null(reimbursement.Total);
        Assert.Null(reimbursement.ActiveDevices);
        Assert.Null(reimbursement.ReimbursementStartDate);
        Assert.Null(reimbursement.ReimbursementEndDate);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    public void A_boolean_attribute_is_true_only_for_a_non_zero_integer(string value, bool expected) =>
        Assert.Equal(
            expected,
            Assert.Single(Parse($"""<row is_reimbursement="{value}" />""").Reimbursements).IsReimbursement);

    [Fact]
    public void Every_attribute_of_a_row_is_preserved_as_raw_json()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Assert.Single(Parse("""<row total="12.34" unmapped_attribute="keep me" />""").Reimbursements)
                .RawAttributesJson!)!;

        Assert.Equal("12.34", raw["total"]);
        Assert.Equal("keep me", raw["unmapped_attribute"]);
    }

    [Fact]
    public void A_document_whose_root_is_a_row_is_parsed_as_that_single_row() =>
        Assert.Equal(
            "C-1",
            Assert.Single(Parse("""<row customer_id="C-1" />""").Reimbursements).CustomerId);

    /// <summary>A report exported as bare sibling rows is not a valid XML document; it still imports.</summary>
    [Fact]
    public void A_fragment_of_sibling_rows_is_parsed_by_wrapping_it_in_a_root_element() =>
        Assert.Equal(
            ["C-1", "C-2"],
            Parse("""<row customer_id="C-1" /><row customer_id="C-2" />""")
                .Reimbursements.Select(reimbursement => reimbursement.CustomerId));

    /// <summary>
    /// A malformed file is refused as unreadable rather than parsed into half a settlement
    /// statement. The caller decides what that means for the file it was reading.
    /// </summary>
    [Fact]
    public void A_malformed_file_is_refused() =>
        Assert.Throws<XmlException>(() => Parse("<row customer_id=\"C-1\""));

    [Fact]
    public void A_well_formed_file_with_no_row_element_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Parse("""<root><notARow customer_id="C-1" /></root>"""));

    /// <summary>
    /// An element that names one attribute twice - the same local name in two namespaces - cannot
    /// have its raw-attribute record made unambiguously, and nothing here may decide which half of
    /// a settlement figure to keep, so the file is refused like any other unreadable one.
    /// </summary>
    [Fact]
    public void An_element_naming_one_attribute_twice_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Parse(
            """<row xmlns:a="urn:a" xmlns:b="urn:b" a:id="first" b:id="second" />"""));

    /// <summary>
    /// The content hash is the import's idempotency key, so it must be the SHA-256 of the file's
    /// bytes and must not depend on the file's name.
    /// </summary>
    [Fact]
    public void The_content_hash_is_the_sha256_of_the_bytes_and_ignores_the_file_name()
    {
        const string content = """<row customer_id="C-1" />""";

        var first = Parse(content);
        var second = Parse(content, "august-copy.xml");

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(expected, first.ContentHash);
        Assert.Equal(expected, second.ContentHash);
    }
}
