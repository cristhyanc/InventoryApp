using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// Whether a stored Nayax sale counts as a completed sale (issue #1105): the one place the
/// completed-sale invariant is written now that a queue-delivered sale can be contradicted.
///
/// <para><strong>The rule.</strong> A completed sale is status <c>12</c>
/// (<see cref="NayaxTransactionStatusClassifier.IsCompletedSale"/>) <em>and</em> its
/// <see cref="NayaxSqsVerification"/> is not <see cref="NayaxSqsVerification.Conflict"/> or
/// <see cref="NayaxSqsVerification.OwnerExcluded"/>. It applies everywhere a completed sale is read:
/// sale costing, the inventory-cost ledger, the machine-stock ledger, reorder needs and every
/// report. A sale with no verification value - every sale <c>lastSales</c> or an uploaded export
/// stored - is decided by its status alone, exactly as before.</para>
///
/// <para><strong>The only automatic exclusion is an authoritative contradiction.</strong>
/// <see cref="NayaxSqsVerification.Conflict"/> is written only when the business's own
/// <c>lastSales</c> returned the same transaction with a different machine, amount or product (see
/// <see cref="NayaxSaleProviderComparisonPolicy"/>). <see cref="NayaxSqsVerification.NeedsInvestigation"/>
/// - which an absence from <c>lastSales</c> can cause - keeps counting, because a missing sale is
/// never proof that it was fake. The other exclusion is the business Owner's own audited decision.</para>
///
/// <para>The EF-translatable form of this rule is <c>EfNayaxSalesQueries.CompletedSalePredicate</c>
/// in Infrastructure; the two must say the same thing, and a test pins them together.</para>
/// </summary>
public static class NayaxSaleCountingPolicy
{
    /// <summary>
    /// Whether <paramref name="verification"/> on its own keeps a sale out of every completed-sale
    /// total, whatever its status.
    /// </summary>
    public static bool IsExcluded(NayaxSqsVerification? verification) =>
        verification is NayaxSqsVerification.Conflict or NayaxSqsVerification.OwnerExcluded;

    /// <summary>Whether a stored sale counts as a completed sale.</summary>
    public static bool IsCountedSale(int? transactionStatusId, NayaxSqsVerification? verification) =>
        NayaxTransactionStatusClassifier.IsCompletedSale(transactionStatusId) && !IsExcluded(verification);

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
    /// </summary>
    public static bool ChangesCounting(NayaxSqsVerification? from, NayaxSqsVerification? to) =>
        IsExcluded(from) != IsExcluded(to);
}
