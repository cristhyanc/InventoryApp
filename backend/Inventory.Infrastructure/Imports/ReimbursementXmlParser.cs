using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Inventory.Application.Imports;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// The one parser of Nayax reimbursement report XML (issue #299, extracted here by issue #1149).
///
/// <para><strong>It is the one parser, and that is the point.</strong> It was extracted when a
/// reimbursement report reached this application two ways - the server's pending folder (retired by
/// issue #1237) and an uploaded Nayax ZIP (<see cref="ZipReimbursementArchiveReader"/>) - because a
/// second parser would be a second set of decisions about what a settlement figure means. Every one
/// of those decisions lives here, and the reader restates none of them.</para>
///
/// <para><strong>The decisions imported financial data depends on, unchanged since issue
/// #299.</strong> Attribute values are read with <see cref="CultureInfo.InvariantCulture"/>; a date
/// without an offset is assumed to be UTC; a numeric attribute is a boolean when it parses to a
/// non-zero integer; an unparsable number or date stays <see langword="null"/> rather than becoming
/// zero; every attribute is preserved as raw JSON so the imported fact stays auditable; a root
/// <c>row</c> element is the single row; and a file that is an XML fragment rather than a document
/// is retried wrapped in a root element, so a report exported as bare <c>row</c> elements - which is
/// exactly what Nayax sends - still imports.</para>
///
/// <para><strong>It throws rather than deciding what a failure means.</strong> Translating a
/// malformed file into "this file failed" is the calling adapter's job at its own boundary
/// (docs/architecture.md § Exception ownership table): an uploaded archive records a per-file
/// outcome against the job and imports the reports beside it.</para>
/// </summary>
public static class ReimbursementXmlParser
{
    /// <summary>
    /// Parses one file's bytes into the facts the import persists.
    /// </summary>
    /// <param name="fileName">The name to record for provenance; never a path.</param>
    /// <param name="bytes">The file's bytes, hashed as they are.</param>
    /// <exception cref="XmlException">The bytes are not XML, wrapped or otherwise.</exception>
    /// <exception cref="InvalidDataException">
    /// The XML carries no <c>row</c> element, or an element names one attribute twice - the same
    /// local name in two namespaces - so its raw-attribute record cannot be made unambiguously.
    /// </exception>
    public static PendingReimbursementXmlFile Parse(string fileName, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));

        var document = ParseDocument(bytes);
        var rows = document.Root?.Name.LocalName == "row"
            ? new List<XElement> { document.Root }
            : document.Descendants().Where(element => element.Name.LocalName == "row").ToList();

        var reimbursements = rows.Select(ParseReimbursement).ToList();
        if (reimbursements.Count == 0)
            throw new InvalidDataException("The XML file does not contain a row element.");

        return new PendingReimbursementXmlFile(fileName, contentHash, reimbursements);
    }

    /// <summary>
    /// Parses the file as an XML document, retrying it wrapped in a root element so a report
    /// that is a sequence of bare <c>row</c> fragments still imports.
    /// </summary>
    private static XDocument ParseDocument(byte[] bytes)
    {
        var xml = Encoding.UTF8.GetString(bytes);
        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return XDocument.Parse($"<root>{xml}</root>", LoadOptions.PreserveWhitespace);
        }
    }

    private static ImportedReimbursementFacts ParseReimbursement(XElement row) => new()
    {
        ReportType = Attr(row, "report_type"),
        ReimbursementStartDate = Date(row, "reimbursement_start_date"),
        ReimbursementEndDate = Date(row, "reimbursement_end_date"),
        ReimbursementPayoutDate = Date(row, "reimbursement_payout_date"),
        CompanyName = Attr(row, "company_name"),
        CustomerId = Attr(row, "customer_id"),
        IsReimbursement = Bool(row, "is_reimbursement"),
        IsInvoice = Bool(row, "is_invoice"),
        Email = Attr(row, "email"),
        ActiveDevices = Int(row, "active_devices"),
        TotalDevices = Int(row, "total_devices"),
        InvoicePaymentMethod = Attr(row, "invoice_payment_method"),
        InvoicePaymentMethodId = Attr(row, "invoice_payment_method_id"),
        Total = Decimal(row, "total"),
        SupportEmail = Attr(row, "support_email"),
        ErpId = Attr(row, "erp_id"),
        DistributorActorId = Attr(row, "distributor_actor_id"),
        FinanceEntityId = Attr(row, "finance_entity_id"),
        RawAttributesJson = Attributes(row),
        Devices = Children(row, "device", element => new ImportedReimbursementDeviceFacts
        {
            EntityId = Attr(element, "entity_id"),
            MachineType = Attr(element, "machine_type"),
            VerticalTypeName = Attr(element, "vertical_type_name"),
            VerticalProductTypeName = Attr(element, "vertical_product_type_name"),
            HardwareSerial = Attr(element, "hw_serial"),
            MachineNumber = Attr(element, "machine_number"),
            ActorCode = Attr(element, "actor_code"),
            Location = Attr(element, "location"),
            TotalBillableTransactionCount = Int(element, "total_billable_trans_count"),
            TotalBillableTransactionAmount = Decimal(element, "total_billable_trans_amount"),
            TotalNotBillableTransactionCount = Int(element, "total_not_billable_trans_count"),
            TotalNotBillableTransactionAmount = Decimal(element, "total_not_billable_trans_amount"),
            ServiceFee = Decimal(element, "service_fee"),
            ProcessingFee = Decimal(element, "processing_fee"),
            HasServicePerTransaction = Bool(element, "has_service_per_transaction"),
            TotalExtraCharge = Decimal(element, "total_extra_charge"),
            NetAmount = Decimal(element, "net_amount"),
            RawAttributesJson = Attributes(element),
        }),
        DevicePayments = Children(row, "devicePayments", element => new ImportedDevicePaymentFacts
        {
            EntityId = Attr(element, "entity_id"),
            PaymentMethodDescription = Attr(element, "payment_method_descr"),
            RecognitionDescription = Attr(element, "recognition_descr"),
            SalesCount = Int(element, "sales_count"),
            TotalSum = Decimal(element, "total_sum"),
            ProcessingFees = Decimal(element, "processing_fees"),
            ServiceFees = Decimal(element, "service_fees"),
            RawAttributesJson = Attributes(element),
        }),
        Fees = Children(row, "fees", element => new ImportedFeeFacts
        {
            FeesTypeId = Attr(element, "fees_type_id"),
            FeeTypeDescription = Attr(element, "fee_type_descr"),
            IsService = Bool(element, "is_service"),
            TotalSum = Decimal(element, "total_sum"),
            TotalSumWithVat = Decimal(element, "total_sum_with_vat"),
            VatPercentage = Decimal(element, "vat_percentage"),
            AverageFeeAmount = Decimal(element, "avg_fee_amount"),
            TotalCount = Decimal(element, "total_count"),
            IsPreviousPeriod = Bool(element, "is_previous_period"),
            RawAttributesJson = Attributes(element),
        }),
        PaymentMethods = Children(row, "paymentMethods", element => new ImportedPaymentMethodFacts
        {
            PaymentMethodId = Attr(element, "payment_method_id"),
            PaymentMethodDescription = Attr(element, "payment_method_descr"),
            IsNayaxReimbursement = Bool(element, "is_nayax_reimbursement"),
            BillingProvider = Attr(element, "billing_provider"),
            TotalSalesCount = Int(element, "total_sales_count"),
            TotalSalesSum = Decimal(element, "total_sales_sum"),
            RecognitionDescription = Attr(element, "recognition_descr"),
            IsPreviousPeriod = Bool(element, "is_previous_period"),
            RawAttributesJson = Attributes(element),
        }),
    };

    /// <summary>
    /// The row's direct children of one element name, in document order, mapped by
    /// <paramref name="map"/>. Only direct children count, as before: a nested element belongs
    /// to its own parent row.
    /// </summary>
    private static IReadOnlyList<T> Children<T>(XElement row, string name, Func<XElement, T> map) =>
        row.Elements().Where(element => element.Name.LocalName == name).Select(map).ToList();

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    /// <summary>
    /// Every attribute of the element as raw JSON, keyed by local name, so the imported fact stays
    /// auditable - and a refusal when that record cannot be made unambiguously.
    ///
    /// <para>An element may carry two attributes with the same local name in different namespaces
    /// (<c>a:id</c> and <c>b:id</c>), which a record keyed by local name cannot hold: one value would
    /// have to be dropped, and nothing here may decide which half of a settlement figure to keep.
    /// It is therefore this file's own parse failure, declared as
    /// <see cref="InvalidDataException"/> like the row-less file above, so each caller records it the
    /// way it records any unreadable file. Nothing Nayax sends uses a namespace at all; an uploaded
    /// file can, and before issue #1149's review this escaped as an <see cref="ArgumentException"/>
    /// that failed the whole upload. The message names nothing out of the file.</para>
    /// </summary>
    private static string? Attributes(XElement element)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var attribute in element.Attributes())
        {
            if (!attributes.TryAdd(attribute.Name.LocalName, attribute.Value))
            {
                throw new InvalidDataException(
                    "The XML file names one attribute twice on the same element.");
            }
        }

        return JsonSerializer.Serialize(attributes);
    }

    private static bool Bool(XElement element, string name) =>
        int.TryParse(Attr(element, name), out var value) && value != 0;

    private static int? Int(XElement element, string name) =>
        int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? Decimal(XElement element, string name) =>
        decimal.TryParse(Attr(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? Date(XElement element, string name) =>
        DateTime.TryParse(Attr(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
