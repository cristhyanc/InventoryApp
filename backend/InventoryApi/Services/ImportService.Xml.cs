using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed partial class ImportService
{
    public async Task<ImportedFileImportResult> ImportPendingXmlFilesAsync()
    {
        var folder = Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "ImportedFiles");
        Directory.CreateDirectory(folder);

        var importedFiles = 0;
        var importedReimbursements = 0;
        var skippedFiles = 0;
        var failedFiles = 0;

        foreach (var path in Directory.EnumerateFiles(folder, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (await _db.ImportedFiles.AnyAsync(f => f.FileHash == hash))
                {
                    File.Delete(path);
                    skippedFiles++;
                    continue;
                }

                var document = ParseDocument(bytes);
                var importedFile = new ImportedFile
                {
                    FileName = Path.GetFileName(path),
                    FileHash = hash,
                    ImportedAt = DateTime.UtcNow
                };

                var rows = document.Root?.Name.LocalName == "row"
                    ? new[] { document.Root }
                    : document.Descendants().Where(element => element.Name.LocalName == "row");
                foreach (var row in rows)
                {
                    if (row is null) continue;
                    var reimbursement = ParseReimbursement(row, importedFile);
                    _db.ImportedReimbursements.Add(reimbursement);
                    importedReimbursements++;
                }

                if (importedFile.Reimbursements.Count == 0)
                    throw new InvalidDataException("The XML file does not contain a row element.");

                _db.ImportedFiles.Add(importedFile);
                await _db.SaveChangesAsync();
                File.Delete(path);
                importedFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or JsonException or System.Xml.XmlException)
            {
                failedFiles++;
                _logger.LogError(ex, "Could not import XML file {FilePath}", path);
            }
        }

        return new ImportedFileImportResult(importedFiles, importedReimbursements, skippedFiles, failedFiles);
    }

    private static XDocument ParseDocument(byte[] bytes)
    {
        var xml = System.Text.Encoding.UTF8.GetString(bytes);
        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return XDocument.Parse($"<root>{xml}</root>", LoadOptions.PreserveWhitespace);
        }
    }

    private static ImportedReimbursement ParseReimbursement(XElement row, ImportedFile file)
    {
        var reimbursement = new ImportedReimbursement
        {
            ImportedFile = file,
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
            RawAttributesJson = Attributes(row)
        };

        foreach (var element in row.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "device":
                    reimbursement.Devices.Add(new ImportedReimbursementDevice
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
                        RawAttributesJson = Attributes(element)
                    });
                    break;
                case "devicePayments":
                    reimbursement.DevicePayments.Add(new ImportedDevicePayment
                    {
                        EntityId = Attr(element, "entity_id"),
                        PaymentMethodDescription = Attr(element, "payment_method_descr"),
                        RecognitionDescription = Attr(element, "recognition_descr"),
                        SalesCount = Int(element, "sales_count"),
                        TotalSum = Decimal(element, "total_sum"),
                        ProcessingFees = Decimal(element, "processing_fees"),
                        ServiceFees = Decimal(element, "service_fees"),
                        RawAttributesJson = Attributes(element)
                    });
                    break;
                case "fees":
                    reimbursement.Fees.Add(new ImportedFee
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
                        RawAttributesJson = Attributes(element)
                    });
                    break;
                case "paymentMethods":
                    reimbursement.PaymentMethods.Add(new ImportedPaymentMethod
                    {
                        PaymentMethodId = Attr(element, "payment_method_id"),
                        PaymentMethodDescription = Attr(element, "payment_method_descr"),
                        IsNayaxReimbursement = Bool(element, "is_nayax_reimbursement"),
                        BillingProvider = Attr(element, "billing_provider"),
                        TotalSalesCount = Int(element, "total_sales_count"),
                        TotalSalesSum = Decimal(element, "total_sales_sum"),
                        RecognitionDescription = Attr(element, "recognition_descr"),
                        IsPreviousPeriod = Bool(element, "is_previous_period"),
                        RawAttributesJson = Attributes(element)
                    });
                    break;
            }
        }

        return reimbursement;
    }

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);
    private static string? Attributes(XElement element) => JsonSerializer.Serialize(element.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));
    private static bool Bool(XElement element, string name) => int.TryParse(Attr(element, name), out var value) && value != 0;
    private static int? Int(XElement element, string name) => int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static decimal? Decimal(XElement element, string name) => decimal.TryParse(Attr(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static DateTime? Date(XElement element, string name) => DateTime.TryParse(Attr(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
