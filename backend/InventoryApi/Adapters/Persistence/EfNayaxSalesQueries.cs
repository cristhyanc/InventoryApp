using System.Linq.Expressions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Persistence;

internal static class EfNayaxSalesQueries
{
    public static Expression<Func<NayaxSales, bool>> CompletedSalePredicate =>
        sale => sale.TransactionStatusId == NayaxTransactionStatusIds.Completed;
}
