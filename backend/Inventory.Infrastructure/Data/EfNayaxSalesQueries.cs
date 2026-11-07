using System.Linq.Expressions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
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
///
/// It is <c>internal</c> because every one of those callers is an Infrastructure persistence
/// adapter. Issue #308 had to make it <c>public</c> while the sale-costing, inventory-cost-ledger
/// and site-commission stores were still API-owned and called it across the assembly boundary;
/// issue #309 brought them into this assembly, and issue #154 narrowed the modifier back, so an
/// expression over the EF model cannot be taken out of the layer that can translate it.
/// </summary>
internal static class EfNayaxSalesQueries
{
    /// <summary>
    /// Status 12 <em>and</em> not kept out by issue #1105's review: a queue-delivered sale the
    /// business's own <c>lastSales</c> contradicted (<see cref="NayaxSqsVerification.Conflict"/>) or
    /// that the business Owner excluded (<see cref="NayaxSqsVerification.OwnerExcluded"/>) is not a
    /// completed sale. This is the translatable form of
    /// <see cref="NayaxSaleCountingPolicy.IsCountedSale"/>, and the two must agree. A sale with no
    /// verification value - every <c>lastSales</c> and uploaded-export sale - is decided by its
    /// status alone, exactly as before.
    /// </summary>
    public static Expression<Func<NayaxSales, bool>> CompletedSalePredicate =>
        sale => sale.TransactionStatusId == NayaxTransactionStatusIds.Completed
            && (sale.SqsVerification == null
                || (sale.SqsVerification != NayaxSqsVerification.Conflict
                    && sale.SqsVerification != NayaxSqsVerification.OwnerExcluded));
}
