using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Inventory.Application.Imports;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Reads pending Nayax reimbursement XML files from the local filesystem and parses them into
/// <see cref="PendingReimbursementXmlFile"/> facts (issue #299), behind the Application's
/// <see cref="IPendingReimbursementXmlSource"/> port.
///
/// Discovery, SHA-256 hashing, XML parsing and removal all moved here unchanged from
/// <c>InventoryApi.Services.ImportService.ImportPendingXmlFilesAsync</c>/<c>ParseDocument</c>/
/// <c>ParseReimbursement</c>, including the deliberate parsing decisions imported financial data
/// depends on: attribute values are read with <see cref="CultureInfo.InvariantCulture"/>, a date
/// without an offset is assumed to be UTC, a numeric attribute is a boolean when it parses to a
/// non-zero integer, an unparsable number or date stays <c>null</c> rather than becoming zero,
/// and a file that is an XML fragment rather than a document is retried wrapped in a root
/// element so a report exported as bare <c>row</c> elements still imports.
///
/// Filesystem and XML failures are translated here and never cross the port: an unreadable or
/// malformed file is logged and answered as <c>null</c>, a file that cannot be deleted is logged
/// and answered as <c>false</c>. Caller cancellation is excluded from that translation and stays
/// cancellation.
/// </summary>
public sealed class FileSystemPendingReimbursementXmlSource : IPendingReimbursementXmlSource
{
    /// <summary>The web-root-relative folder pending reimbursement files are collected in.</summary>
    public const string PendingFolderName = "ImportedFiles";

    private readonly string _folder;
    private readonly ILogger<FileSystemPendingReimbursementXmlSource> _logger;

    public FileSystemPendingReimbursementXmlSource(
        PendingReimbursementXmlOptions options,
        ILogger<FileSystemPendingReimbursementXmlSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentRootPath);

        var webRoot = options.WebRootPath ?? Path.Combine(options.ContentRootPath, "wwwroot");
        _folder = Path.Combine(webRoot, PendingFolderName);
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListPendingFiles()
    {
        // Created rather than probed, exactly as before: the folder is this import's inbox, and
        // an operator who has not dropped a file in yet is not an error.
        Directory.CreateDirectory(_folder);

        return Directory.EnumerateFiles(_folder, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<PendingReimbursementXmlFile?> ReadAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = ConfineToFolder(fileName);
        if (path is null)
        {
            _logger.LogError("Could not import XML file {FileName}: it does not name a file in the pending folder.", fileName);
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var contentHash = Convert.ToHexString(SHA256.HashData(bytes));

            var document = ParseDocument(bytes);
            var rows = document.Root?.Name.LocalName == "row"
                ? new List<XElement> { document.Root }
                : document.Descendants().Where(element => element.Name.LocalName == "row").ToList();

            var reimbursements = rows.Select(ParseReimbursement).ToList();
            if (reimbursements.Count == 0)
                throw new InvalidDataException("The XML file does not contain a row element.");

            return new PendingReimbursementXmlFile(Path.GetFileName(path), contentHash, reimbursements);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException
            or JsonException
            or XmlException)
        {
            // The file name, never the server path: a log line is not a place to publish where
            // business documents live on disk.
            _logger.LogError(ex, "Could not import XML file {FileName}", fileName);
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryDiscard(string fileName)
    {
        var path = ConfineToFolder(fileName);
        if (path is null)
        {
            _logger.LogError("Could not remove XML file {FileName}: it does not name a file in the pending folder.", fileName);
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not remove imported XML file {FileName}", fileName);
            return false;
        }
    }

    /// <summary>
    /// Reduces a name to its final path segment and proves the result is inside the pending
    /// folder, or answers <c>null</c>. The names this adapter hands out come from its own
    /// enumeration, but they make the round trip through the Application layer, so they are
    /// re-checked here: no caller may choose an arbitrary filesystem path.
    /// </summary>
    private string? ConfineToFolder(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var leaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf) || !string.Equals(leaf, fileName, StringComparison.Ordinal)) return null;

        var folderFullPath = Path.GetFullPath(_folder);
        var candidate = Path.GetFullPath(Path.Combine(folderFullPath, leaf));
        var prefix = folderFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? folderFullPath
            : folderFullPath + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
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

    private static string? Attributes(XElement element) =>
        JsonSerializer.Serialize(element.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));

    private static bool Bool(XElement element, string name) =>
        int.TryParse(Attr(element, name), out var value) && value != 0;

    private static int? Int(XElement element, string name) =>
        int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? Decimal(XElement element, string name) =>
        decimal.TryParse(Attr(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? Date(XElement element, string name) =>
        DateTime.TryParse(Attr(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
