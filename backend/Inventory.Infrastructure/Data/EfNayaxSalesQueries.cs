using System.Linq.Expressions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// The one EF-translatable completed-sale filter, beside the <see cref="AppDbContext"/> whose
/// entities it is written against (issue #308, Persistence 7/8 of #153).
///
/// It is persistence, not Domain: <see cref="NayaxTransactionStatusIds.Completed"/> remains the
/// single authoritative "status 12 is an approved sale" rule, and this expression is only how a
/// translated query applies it to the <see cref="NayaxSales"/> entity. Keeping it here is what lets
/// the reporting fact providers, the sale-costing/inventory-cost-ledger stores and the
/// site-commission store all select completed sales through one predicate rather than repeating a
/// status comparison per query.
/// </summary>
public static class EfNayaxSalesQueries
{
    public static Expression<Func<NayaxSales, bool>> CompletedSalePredicate =>
        sale => sale.TransactionStatusId == NayaxTransactionStatusIds.Completed;
}
