using System.Linq.Expressions;
using InventoryApi.Models;

namespace InventoryApi.Services;

public enum NayaxTransactionStatus
{
    Unknown,
    Completed,
    Pending,
    Refunded,
    CancelledOrDeclined
}

public static class NayaxTransactionStatusIds
{
    public const int Completed = 12;
    public const int CancelledOrDeclined26 = 26;
    public const int CashlessCancelledProductNotDispensed = 28;
    public const int CancelledOrDeclined31 = 31;
    public const int PendingSettlementNotFinal = 55;
    public const int Refunded = 62;
    public const int PendingBatch = 80;
    public const int CancelledOrDeclined250 = 250;
}

public static class NayaxTransactionStatusClassifier
{
    public static Expression<Func<NayaxSales, bool>> CompletedSalePredicate =>
        sale => sale.TransactionStatusId == NayaxTransactionStatusIds.Completed;

    public static NayaxTransactionStatus Classify(int? transactionStatusId) =>
        transactionStatusId switch
        {
            NayaxTransactionStatusIds.Completed => NayaxTransactionStatus.Completed,
            NayaxTransactionStatusIds.PendingSettlementNotFinal or NayaxTransactionStatusIds.PendingBatch => NayaxTransactionStatus.Pending,
            NayaxTransactionStatusIds.Refunded => NayaxTransactionStatus.Refunded,
            NayaxTransactionStatusIds.CancelledOrDeclined26 or
            NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed or
            NayaxTransactionStatusIds.CancelledOrDeclined31 or
            NayaxTransactionStatusIds.CancelledOrDeclined250 => NayaxTransactionStatus.CancelledOrDeclined,
            null => NayaxTransactionStatus.Unknown,
            _ => NayaxTransactionStatus.Unknown
        };

    public static bool IsCompletedSale(NayaxSales sale) =>
        Classify(sale.TransactionStatusId) == NayaxTransactionStatus.Completed;

    public static string Describe(int? transactionStatusId) =>
        transactionStatusId switch
        {
            NayaxTransactionStatusIds.Completed => "Approved / Completed",
            NayaxTransactionStatusIds.PendingSettlementNotFinal => "Pending / Settlement not final",
            NayaxTransactionStatusIds.Refunded => "Refunded",
            NayaxTransactionStatusIds.CancelledOrDeclined26 => "Cancelled / declined",
            NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed => "Cashless cancelled - Product could not be dispensed",
            NayaxTransactionStatusIds.CancelledOrDeclined31 => "Cancelled / declined",
            NayaxTransactionStatusIds.PendingBatch => "Pending batch",
            NayaxTransactionStatusIds.CancelledOrDeclined250 => "Cancelled / declined",
            null => "Unknown",
            _ => $"Status {transactionStatusId}"
        };
}
