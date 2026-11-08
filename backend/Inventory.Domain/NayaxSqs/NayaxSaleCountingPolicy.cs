using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// The two questions a stored Nayax sale is read for, and the one place each is answered: does it
/// count as a completed sale (issue #1105), and does it consume stock (issue #1107)?
///
/// <para><strong>They used to be one rule and the owner's refund decision of 8 November 2026 split
/// them.</strong> When Nayax refunds a sale the money is reversed - the sale leaves revenue, the
/// card and cash totals, reconciliation and every completed-sale report, exactly as status
/// <c>62</c> behaves - but <em>the item is still gone</em>: the machine dispensed it and the
/// business no longer owns it. One rule could not say both things, so there are now two.</para>
///
/// <para><strong>Completed sale</strong> (<see cref="IsCountedSale"/>), which is money and reports:
/// status <c>12</c> (<see cref="NayaxTransactionStatusClassifier.IsCompletedSale"/>) <em>and</em> a
/// <see cref="NayaxSqsVerification"/> that is not <see cref="NayaxSqsVerification.Conflict"/> or
/// <see cref="NayaxSqsVerification.OwnerExcluded"/>. Unchanged by issue #1107: every report, every
/// revenue, card, cash and reconciliation total, and reorder/par need reads this one, because a
/// refund is not demand.</para>
///
/// <para><strong>Consumes stock</strong> (<see cref="ConsumesStock"/>), which is physical machine
/// stock and business-owned costing inventory: status <c>12</c> <strong>or</strong> status
/// <c>62</c>, with the same two exclusions. Sale costing, the inventory-cost ledger and the
/// machine-stock ledger read this one, so a refunded sale keeps the unit it consumed, at its
/// original cost.</para>
///
/// <para><strong>The exclusions are shared, and that is deliberate.</strong> A sale the business's
/// own <c>lastSales</c> contradicted, or one the Owner excluded, says nothing reliable about what
/// left the machine either - so it consumes nothing, exactly as it counts for nothing.
/// <see cref="NayaxSqsVerification.Conflict"/> is written only for an authoritative contradiction
/// about the machine, the amount or the product (see
/// <see cref="NayaxSaleProviderComparisonPolicy"/>);
/// <see cref="NayaxSqsVerification.NeedsInvestigation"/> - which an absence from <c>lastSales</c>
/// can cause - keeps both answers, because a missing sale is never proof that it was fake. A sale
/// with no verification value - every sale <c>lastSales</c> or an uploaded export stored - is
/// decided by its status alone.</para>
///
/// <para>Each rule has one EF-translatable form in Infrastructure -
/// <c>EfNayaxSalesQueries.CompletedSalePredicate</c> and
/// <c>EfNayaxSalesQueries.StockConsumingSalePredicate</c> - and each pair must say the same thing;
/// a test pins each of them together.</para>
/// </summary>
public static class NayaxSaleCountingPolicy
{
    /// <summary>
    /// Whether <paramref name="verification"/> on its own keeps a sale out of every completed-sale
    /// total, whatever its status.
    /// </summary>
    public static bool IsExcluded(NayaxSqsVerification? verification) =>
        verification is NayaxSqsVerification.Conflict or NayaxSqsVerification.OwnerExcluded;

    /// <summary>Whether a stored sale counts as a completed sale: money and every report.</summary>
    public static bool IsCountedSale(int? transactionStatusId, NayaxSqsVerification? verification) =>
        NayaxTransactionStatusClassifier.IsCompletedSale(transactionStatusId) && !IsExcluded(verification);

    /// <summary>
    /// Whether a stored sale consumed a unit: physical machine stock and business-owned costing
    /// inventory (issue #1107).
    ///
    /// <para>Status <c>12</c> or status <c>62</c>, neither excluded by
    /// <see cref="IsExcluded"/>. A refunded sale dispensed its unit and the business no longer owns
    /// it, so it keeps consuming exactly what it consumed before the refund - at its original cost,
    /// through this same rule rather than a separate refund cost line. Every other status consumes
    /// nothing: a pending, cancelled, declined or unknown transaction never dispensed anything.</para>
    ///
    /// <para>It is deliberately <em>not</em> expressed as "counts, or is refunded": a refunded sale
    /// the Owner excluded consumes nothing, and writing the rule as one condition over the status
    /// and the verification is what makes that true rather than remembered.</para>
    /// </summary>
    public static bool ConsumesStock(int? transactionStatusId, NayaxSqsVerification? verification) =>
        transactionStatusId is NayaxTransactionStatusIds.Completed or NayaxTransactionStatusIds.Refunded
        && !IsExcluded(verification);

    /// <summary>
    /// The sale's status for status reporting: the transaction status, except that a status-12 sale
    /// this policy excludes reports <see cref="NayaxTransactionStatus.ExcludedByReview"/>, so it is
    /// visible as excluded rather than silently missing or silently counted.
    /// </summary>
    public static NayaxTransactionStatus Classify(int? transactionStatusId, NayaxSqsVerification? verification)
    {
        var status = NayaxTransactionStatusClassifier.Classify(transactionStatusId);
        return status is NayaxTransactionStatus.Completed && IsExcluded(verification)
            ? NayaxTransactionStatus.ExcludedByReview
            : status;
    }

    /// <summary>
    /// The status description status reporting shows: the transaction status's own, unless the sale
    /// is excluded, when it says why.
    /// </summary>
    public static string Describe(int? transactionStatusId, NayaxSqsVerification? verification) =>
        Classify(transactionStatusId, verification) is NayaxTransactionStatus.ExcludedByReview
            ? verification is NayaxSqsVerification.OwnerExcluded
                ? "Not counted - excluded by the owner"
                : "Not counted - contradicted by Nayax"
            : NayaxTransactionStatusClassifier.Describe(transactionStatusId);

    /// <summary>
    /// Whether moving a sale from <paramref name="from"/> to <paramref name="to"/> changes whether it
    /// counts - the question that decides whether its product's inventory cost must be rebuilt.
    ///
    /// <para>It answers the stock question at the same time, and that is a consequence of the two
    /// rules sharing <see cref="IsExcluded"/> rather than a coincidence: a verification change moves
    /// a sale into or out of <em>both</em> totals together, whatever its status. A
    /// <em>status</em> change from <c>12</c> to <c>62</c> is the opposite case - it changes counting
    /// and leaves stock consumption exactly as it was - and is not what this answers.</para>
    /// </summary>
    public static bool ChangesCounting(NayaxSqsVerification? from, NayaxSqsVerification? to) =>
        IsExcluded(from) != IsExcluded(to);
}
