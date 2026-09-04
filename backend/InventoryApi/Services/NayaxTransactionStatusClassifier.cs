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

public static class NayaxTransactionStatusClassifier
{
    public static Expression<Func<NayaxSales, bool>> CompletedSalePredicate =>
        sale => sale.TransactionStatusId == 12;

    public static NayaxTransactionStatus Classify(int? transactionStatusId) =>
        transactionStatusId switch
        {
            12 => NayaxTransactionStatus.Completed,
            55 or 80 => NayaxTransactionStatus.Pending,
            62 => NayaxTransactionStatus.Refunded,
            null => NayaxTransactionStatus.Unknown,
            _ => NayaxTransactionStatus.Unknown
        };

    public static bool IsCompletedSale(NayaxSales sale) =>
        Classify(sale.TransactionStatusId) == NayaxTransactionStatus.Completed;

    public static string Describe(int? transactionStatusId) =>
        transactionStatusId switch
        {
            12 => "Approved / Completed",
            55 => "Pending / Settlement not final",
            62 => "Refunded",
            80 => "Pending batch",
            null => "Unknown",
            _ => $"Status {transactionStatusId}"
        };
}
