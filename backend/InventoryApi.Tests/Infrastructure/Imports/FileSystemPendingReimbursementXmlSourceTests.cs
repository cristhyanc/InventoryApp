using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inventory.Infrastructure.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// <see cref="FileSystemPendingReimbursementXmlSource"/> (issue #299) owns the discovery,
/// hashing and parsing the former <c>ImportService.ImportPendingXmlFilesAsync</c>/
/// <c>ParseReimbursement</c> did. These tests pin the parsing decisions imported Nayax
/// reimbursement data depends on - invariant-culture numbers, UTC-assumed dates, numeric
/// booleans, unparsable values staying unknown rather than zero - and the failure answers the
/// port promises instead of letting a filesystem or XML exception escape.
/// </summary>
public class FileSystemPendingReimbursementXmlSourceTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "inventoryapp-pending-xml-" + Guid.NewGuid().ToString("N"));

    private string PendingFolder => Path.Combine(_contentRoot, "wwwroot", FileSystemPendingReimbursementXmlSource.PendingFolderName);

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FileSystemPendingReimbursementXmlSource CreateSource() =>
        new(
            new PendingReimbursementXmlOptions { ContentRootPath = _contentRoot },
            NullLogger<FileSystemPendingReimbursementXmlSource>.Instance);

    private void WritePending(string fileName, string content)
    {
        Directory.CreateDirectory(PendingFolder);
        File.WriteAllText(Path.Combine(PendingFolder, fileName), content);
    }

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

    [Fact]
    public async Task A_valid_file_is_parsed_into_its_reimbursement_device_payment_and_fee_facts()
    {
        WritePending("august.xml", FullRow);

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        Assert.NotNull(file);
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
    /// makes this test independent of the host's own time zone: an <see cref="DateTimeKind.Unspecified"/>
    /// result is exactly what dropping the UTC assumption produces, and it reads as local time
    /// everywhere downstream.
    /// </summary>
    [Fact]
    public async Task Dates_without_an_offset_are_read_as_UTC()
    {
        WritePending("august.xml", FullRow);

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        var reimbursement = Assert.Single(file!.Reimbursements);
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
    public async Task Numbers_are_read_with_the_invariant_culture()
    {
        WritePending("august.xml", """<row total="1234.56" active_devices="3" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        var reimbursement = Assert.Single(file!.Reimbursements);
        Assert.Equal(1234.56m, reimbursement.Total);
        Assert.Equal(3, reimbursement.ActiveDevices);
    }

    /// <summary>
    /// An unparsable or absent amount stays unknown. Reading it as zero would turn missing
    /// imported data into a reported financial value.
    /// </summary>
    [Fact]
    public async Task An_unparsable_or_absent_number_or_date_stays_unknown()
    {
        WritePending("august.xml", """<row total="n/a" active_devices="" reimbursement_start_date="not-a-date" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        var reimbursement = Assert.Single(file!.Reimbursements);
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
    public async Task A_boolean_attribute_is_true_only_for_a_non_zero_integer(string value, bool expected)
    {
        WritePending("august.xml", $"""<row is_reimbursement="{value}" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        Assert.Equal(expected, Assert.Single(file!.Reimbursements).IsReimbursement);
    }

    [Fact]
    public async Task Every_attribute_of_a_row_is_preserved_as_raw_json()
    {
        WritePending("august.xml", """<row total="12.34" unmapped_attribute="keep me" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Assert.Single(file!.Reimbursements).RawAttributesJson!)!;
        Assert.Equal("12.34", raw["total"]);
        Assert.Equal("keep me", raw["unmapped_attribute"]);
    }

    [Fact]
    public async Task A_document_whose_root_is_a_row_is_parsed_as_that_single_row()
    {
        WritePending("august.xml", """<row customer_id="C-1" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        Assert.Equal("C-1", Assert.Single(file!.Reimbursements).CustomerId);
    }

    /// <summary>A report exported as bare sibling rows is not a valid XML document; it still imports.</summary>
    [Fact]
    public async Task A_fragment_of_sibling_rows_is_parsed_by_wrapping_it_in_a_root_element()
    {
        WritePending("august.xml", """<row customer_id="C-1" /><row customer_id="C-2" />""");

        var file = await CreateSource().ReadAsync("august.xml", CancellationToken.None);

        Assert.Equal(["C-1", "C-2"], file!.Reimbursements.Select(r => r.CustomerId));
    }

    [Fact]
    public async Task A_malformed_file_is_reported_as_unreadable_and_is_left_in_the_queue()
    {
        WritePending("broken.xml", "<row customer_id=\"C-1\"");

        var source = CreateSource();
        var file = await source.ReadAsync("broken.xml", CancellationToken.None);

        Assert.Null(file);
        Assert.Equal(["broken.xml"], source.ListPendingFiles());
    }

    [Fact]
    public async Task A_well_formed_file_with_no_row_element_is_reported_as_unreadable()
    {
        WritePending("empty.xml", "<root><notARow customer_id=\"C-1\" /></root>");

        Assert.Null(await CreateSource().ReadAsync("empty.xml", CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_reported_as_unreadable()
    {
        Directory.CreateDirectory(PendingFolder);

        Assert.Null(await CreateSource().ReadAsync("missing.xml", CancellationToken.None));
    }

    /// <summary>
    /// The content hash is the import's idempotency key, so it must be the SHA-256 of the file's
    /// bytes and must not depend on the file's name.
    /// </summary>
    [Fact]
    public async Task The_content_hash_is_the_sha256_of_the_bytes_and_ignores_the_file_name()
    {
        const string content = """<row customer_id="C-1" />""";
        WritePending("august.xml", content);
        WritePending("august-copy.xml", content);

        var source = CreateSource();
        var first = await source.ReadAsync("august.xml", CancellationToken.None);
        var second = await source.ReadAsync("august-copy.xml", CancellationToken.None);

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(expected, first!.ContentHash);
        Assert.Equal(expected, second!.ContentHash);
    }

    [Fact]
    public void ListPendingFiles_creates_the_folder_when_it_does_not_exist_and_reports_nothing()
    {
        Assert.Empty(CreateSource().ListPendingFiles());
        Assert.True(Directory.Exists(PendingFolder));
    }

    [Fact]
    public void ListPendingFiles_reports_only_xml_file_names_in_the_pending_folder()
    {
        WritePending("august.xml", "<row />");
        WritePending("notes.txt", "not an import");
        Directory.CreateDirectory(Path.Combine(PendingFolder, "nested"));
        File.WriteAllText(Path.Combine(PendingFolder, "nested", "deeper.xml"), "<row />");

        Assert.Equal(["august.xml"], CreateSource().ListPendingFiles());
    }

    [Fact]
    public void TryDiscard_removes_the_file_and_reports_success()
    {
        WritePending("august.xml", "<row />");
        var source = CreateSource();

        Assert.True(source.TryDiscard("august.xml"));
        Assert.Empty(source.ListPendingFiles());
    }

    [Fact]
    public void TryDiscard_reports_failure_for_a_name_outside_the_pending_folder()
    {
        WritePending("august.xml", "<row />");
        var outside = Path.Combine(_contentRoot, "appsettings.json");
        File.WriteAllText(outside, "{}");

        var source = CreateSource();

        Assert.False(source.TryDiscard(Path.Combine("..", "..", "appsettings.json")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_name_outside_the_pending_folder_is_never_read()
    {
        Directory.CreateDirectory(PendingFolder);
        File.WriteAllText(Path.Combine(_contentRoot, "secrets.xml"), """<row customer_id="C-1" />""");

        Assert.Null(await CreateSource().ReadAsync(
            Path.Combine("..", "..", "secrets.xml"), CancellationToken.None));
    }

    /// <summary>Caller cancellation stays cancellation; it is never translated into a failed file.</summary>
    [Fact]
    public async Task Cancellation_is_not_reported_as_an_unreadable_file()
    {
        WritePending("august.xml", FullRow);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSource().ReadAsync("august.xml", cancellation.Token));
    }
}
