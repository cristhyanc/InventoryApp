using Inventory.Application.SalesSync;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Applying one Nayax SQS refund message (issue #1107), beside the rest of
/// <see cref="EfLatestNayaxSalesStore"/> because it shares that adapter's one
/// <c>(BusinessId, TransactionID)</c> lookup and its <c>BEGIN IMMEDIATE</c> transaction - and
/// differs in everything else, because it writes no sale.
///
/// <para><strong>The one column it may write on a sale is the status.</strong> The product
/// association, the amount, the instant, the costs, the costing status, the provenance and the
/// verification state of the sale it finds are left exactly as they are. It inserts no sale, and a
/// transaction this business has no sale for is reported as unmatched rather than stored: a refund
/// is evidence about a sale, never a sale.</para>
///
/// <para><strong>And it records no pending inventory-cost rebuild.</strong> Status <c>12</c> and
/// status <c>62</c> both consume stock
/// (<see cref="NayaxSaleCountingPolicy.ConsumesStock"/>), so moving between them changes no
/// quantity, no inventory value and no sale cost; there is nothing to replay, and that is exactly
/// what makes the owner's decision - "the stock is gone" - hold without a figure moving. What the
/// refund does change is whether the sale is a completed sale, and every total that depends on that
/// is read live through the completed-sale filter.</para>
/// </summary>
public sealed partial class EfLatestNayaxSalesStore
{
    /// <inheritdoc />
    public async Task<NayaxSqsRefundApplication> ApplySqsRefundAsync(
        NayaxSqsRefundInput refund, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refund);

        // BEGIN IMMEDIATE for the reason PersistSqsSaleOnceAsync takes it: the read this change is
        // decided on and the change itself have to be one operation. Without the write lock the
        // request-driven sync could enrich - or another delivery of this very refund could apply -
        // between the read and the write, and "apply once" would rest on timing rather than on a lock.
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // Read inside the lock, by (BusinessId, TransactionID) with the business half coming from the
        // tenant query filter as everywhere else. Tracked, because the status is written back onto
        // this row; this context belongs to the one message's own scope and has tracked no sale
        // before now, so the query is answered by the database rather than by a stale instance.
        var stored = await _db.NayaxSales
            .FirstOrDefaultAsync(sale => sale.TransactionID == refund.TransactionId, cancellationToken);

        if (stored is null)
        {
            // Nothing is written and nothing is inserted. A refund for a transaction this business
            // never stored is the case the issue's assumption may be wrong about, and leaving it for
            // the dead-letter queue as a sample is how a person gets to see a real one.
            return NayaxSqsRefundApplication.NoMatchingSale;
        }

        var decision = NayaxSaleRefundPolicy.Decide(
            stored.TransactionStatusId,
            new NayaxStoredSaleValues(
                stored.MachineID,
                stored.SettlementValue,

                // The stored instant and the currency are not compared for a refund (see
                // NayaxSaleRefundPolicy): passed as null rather than omitted, so the record keeps one
                // shape and the policy stays the only thing that decides what is compared.
                AuthorizationInstantUtc: null,
                CurrencyCode: null,
                stored.NayaxProductId),
            new NayaxRefundMessageValues(refund.MachineId, refund.SettlementValue, refund.NayaxProductId));

        if (decision.Decision != NayaxSaleRefundDecision.Apply)
        {
            return decision.Decision switch
            {
                NayaxSaleRefundDecision.AlreadyRefunded => NayaxSqsRefundApplication.AlreadyRefunded,
                NayaxSaleRefundDecision.NotRefundable => NayaxSqsRefundApplication.NotRefundable,
                _ => NayaxSqsRefundApplication.Contradicted,
            };
        }

        var previousStatusId = stored.TransactionStatusId;
        stored.TransactionStatusId = NayaxTransactionStatusIds.Refunded;

        _db.NayaxSaleStatusChangeAudits.Add(new NayaxSaleStatusChangeAudit
        {
            NayaxSaleId = stored.Id,
            TransactionId = stored.TransactionID,
            PreviousTransactionStatusId = previousStatusId,
            NewTransactionStatusId = NayaxTransactionStatusIds.Refunded,
            SourceMessageId = refund.MessageId,
            OccurredAtUtc = refund.ReceivedAtUtc,
        });

        // The status and its audit in one SaveChanges, and therefore in the one transaction this
        // method opened, so "the sale is refunded" and "something recorded why" can never disagree.
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return NayaxSqsRefundApplication.Applied;
    }
}
