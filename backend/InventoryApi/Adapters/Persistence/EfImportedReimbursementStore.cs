using Inventory.Application.Imports;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IImportedReimbursementStore"/> (issue #299). It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on
/// <see cref="AppDbContext"/> and the persistence models, which still live there - the same
/// pattern as <see cref="EfSaleCostingStore"/>; it must move once #153 relocates persistence.
///
/// Both operations keep the behaviour the former <c>ImportService.ImportPendingXmlFilesAsync</c>
/// had: the duplicate lookup is the same tenant-filtered <c>ImportedFiles</c> query, with no
/// business predicate of its own, and the write stages the whole reimbursement graph and saves
/// exactly once, so a file is either imported completely or not at all.
/// </summary>
public sealed class EfImportedReimbursementStore : IImportedReimbursementStore
{
    private readonly AppDbContext _db;

    public EfImportedReimbursementStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task<bool> HasFileWithContentHashAsync(string contentHash, CancellationToken cancellationToken) =>
        _db.ImportedFiles.AnyAsync(file => file.FileHash == contentHash, cancellationToken);

    /// <inheritdoc />
    public async Task ImportAsync(
        PendingReimbursementXmlFile file,
        DateTime importedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var importedFile = new ImportedFile
        {
            FileName = file.FileName,
            FileHash = file.ContentHash,
            ImportedAt = importedAtUtc,
        };

        foreach (var facts in file.Reimbursements)
        {
            _db.ImportedReimbursements.Add(ToEntity(facts, importedFile));
        }

        _db.ImportedFiles.Add(importedFile);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Maps the imported facts onto the persisted graph. Ownership is not set here: every row
    /// is stamped centrally from the caller's resolved business on save (issue #64).
    /// </summary>
    private static ImportedReimbursement ToEntity(ImportedReimbursementFacts facts, ImportedFile importedFile)
    {
        var reimbursement = new ImportedReimbursement
        {
            ImportedFile = importedFile,
            ReportType = facts.ReportType,
            ReimbursementStartDate = facts.ReimbursementStartDate,
            ReimbursementEndDate = facts.ReimbursementEndDate,
            ReimbursementPayoutDate = facts.ReimbursementPayoutDate,
            CompanyName = facts.CompanyName,
            CustomerId = facts.CustomerId,
            IsReimbursement = facts.IsReimbursement,
            IsInvoice = facts.IsInvoice,
            Email = facts.Email,
            ActiveDevices = facts.ActiveDevices,
            TotalDevices = facts.TotalDevices,
            InvoicePaymentMethod = facts.InvoicePaymentMethod,
            InvoicePaymentMethodId = facts.InvoicePaymentMethodId,
            Total = facts.Total,
            SupportEmail = facts.SupportEmail,
            ErpId = facts.ErpId,
            DistributorActorId = facts.DistributorActorId,
            FinanceEntityId = facts.FinanceEntityId,
            RawAttributesJson = facts.RawAttributesJson,
        };

        foreach (var device in facts.Devices)
        {
            reimbursement.Devices.Add(new ImportedReimbursementDevice
            {
                EntityId = device.EntityId,
                MachineType = device.MachineType,
                VerticalTypeName = device.VerticalTypeName,
                VerticalProductTypeName = device.VerticalProductTypeName,
                HardwareSerial = device.HardwareSerial,
                MachineNumber = device.MachineNumber,
                ActorCode = device.ActorCode,
                Location = device.Location,
                TotalBillableTransactionCount = device.TotalBillableTransactionCount,
                TotalBillableTransactionAmount = device.TotalBillableTransactionAmount,
                TotalNotBillableTransactionCount = device.TotalNotBillableTransactionCount,
                TotalNotBillableTransactionAmount = device.TotalNotBillableTransactionAmount,
                ServiceFee = device.ServiceFee,
                ProcessingFee = device.ProcessingFee,
                HasServicePerTransaction = device.HasServicePerTransaction,
                TotalExtraCharge = device.TotalExtraCharge,
                NetAmount = device.NetAmount,
                RawAttributesJson = device.RawAttributesJson,
            });
        }

        foreach (var payment in facts.DevicePayments)
        {
            reimbursement.DevicePayments.Add(new ImportedDevicePayment
            {
                EntityId = payment.EntityId,
                PaymentMethodDescription = payment.PaymentMethodDescription,
                RecognitionDescription = payment.RecognitionDescription,
                SalesCount = payment.SalesCount,
                TotalSum = payment.TotalSum,
                ProcessingFees = payment.ProcessingFees,
                ServiceFees = payment.ServiceFees,
                RawAttributesJson = payment.RawAttributesJson,
            });
        }

        foreach (var fee in facts.Fees)
        {
            reimbursement.Fees.Add(new ImportedFee
            {
                FeesTypeId = fee.FeesTypeId,
                FeeTypeDescription = fee.FeeTypeDescription,
                IsService = fee.IsService,
                TotalSum = fee.TotalSum,
                TotalSumWithVat = fee.TotalSumWithVat,
                VatPercentage = fee.VatPercentage,
                AverageFeeAmount = fee.AverageFeeAmount,
                TotalCount = fee.TotalCount,
                IsPreviousPeriod = fee.IsPreviousPeriod,
                RawAttributesJson = fee.RawAttributesJson,
            });
        }

        foreach (var paymentMethod in facts.PaymentMethods)
        {
            reimbursement.PaymentMethods.Add(new ImportedPaymentMethod
            {
                PaymentMethodId = paymentMethod.PaymentMethodId,
                PaymentMethodDescription = paymentMethod.PaymentMethodDescription,
                IsNayaxReimbursement = paymentMethod.IsNayaxReimbursement,
                BillingProvider = paymentMethod.BillingProvider,
                TotalSalesCount = paymentMethod.TotalSalesCount,
                TotalSalesSum = paymentMethod.TotalSalesSum,
                RecognitionDescription = paymentMethod.RecognitionDescription,
                IsPreviousPeriod = paymentMethod.IsPreviousPeriod,
                RawAttributesJson = paymentMethod.RawAttributesJson,
            });
        }

        return reimbursement;
    }
}
