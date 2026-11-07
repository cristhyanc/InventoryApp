using System.Text.Json.Serialization;
using Inventory.Domain.NayaxSqs;

namespace Inventory.Infrastructure.Models;

/// <summary>What one row of <see cref="NayaxSaleOwnerDecisionAudit"/> records (issue #1105).</summary>
public enum NayaxSaleOwnerDecisionAuditKind
{
    /// <summary>A business Owner applied a decision.</summary>
    Decision = 10,

    /// <summary>
    /// Later provider evidence contradicted the decided values and returned the sale to review:
    /// "decision superseded by new evidence".
    /// </summary>
    Superseded = 20,
}

/// <summary>
/// The append-only trail of business Owner decisions about flagged queue-delivered sales, and of
/// later evidence superseding them (issue #1105). Ordinary tenant-owned data: stamped by
/// <c>BusinessOwnershipEnforcer</c>, filtered centrally, and written in the same write-locked
/// transaction as the change it records, so a decision and its audit commit together or not at all.
///
/// <para>Each row names the evidence it rested on by its <see cref="NayaxSaleProviderEvidence"/> id,
/// the state before and after, and the sale's values before and after - so a decision can always be
/// told apart from Nayax's own verification, and what it changed can be read back exactly.</para>
///
/// <para><strong>The reconciled-period note.</strong> When the sale's counted amount changed and
/// an imported reimbursement period covers the sale's instant (before or after), that period's
/// coverage dates and the counted-amount difference are recorded here, because reimbursement
/// reconciliation reads sales live and a period shown as reconciled before the decision may now
/// show a difference.</para>
/// </summary>
public class NayaxSaleOwnerDecisionAudit : IBusinessOwned
{
    public long Id { get; set; }

    [JsonIgnore]
    public int BusinessId { get; set; }

    public long NayaxSaleId { get; set; }

    public long TransactionId { get; set; }

    public NayaxSaleOwnerDecisionAuditKind Kind { get; set; }

    /// <summary>The decision applied, or <see langword="null"/> for a superseded row.</summary>
    public NayaxSaleOwnerDecision? Decision { get; set; }

    /// <summary>The evidence row the decision relied on, or that superseded it.</summary>
    public long EvidenceId { get; set; }

    public string? Reason { get; set; }

    /// <summary>The Owner's directory tenant id, or <see langword="null"/> for a superseded row.</summary>
    public string? ActorDirectoryTenantId { get; set; }

    /// <summary>The Owner's object id, or <see langword="null"/> for a superseded row.</summary>
    public string? ActorObjectId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public NayaxSqsVerification PreviousState { get; set; }

    public NayaxSqsVerification ResultingState { get; set; }

    public long? PreviousMachineId { get; set; }

    public long? ResultingMachineId { get; set; }

    public decimal? PreviousSettlementValue { get; set; }

    public decimal? ResultingSettlementValue { get; set; }

    public DateTime? PreviousAuthorizationInstantUtc { get; set; }

    public DateTime? ResultingAuthorizationInstantUtc { get; set; }

    public long? PreviousNayaxProductId { get; set; }

    public long? ResultingNayaxProductId { get; set; }

    public long? PreviousProductId { get; set; }

    public long? ResultingProductId { get; set; }

    /// <summary>The amount the sale contributed to completed sales before the change (0 when excluded).</summary>
    public decimal PreviousCountedAmount { get; set; }

    /// <summary>The amount the sale contributes to completed sales after the change (0 when excluded).</summary>
    public decimal ResultingCountedAmount { get; set; }

    /// <summary>
    /// The start of the imported reimbursement period the change touched, when the counted amount
    /// changed inside one; <see langword="null"/> otherwise.
    /// </summary>
    public DateTime? ReconciledPeriodFrom { get; set; }

    /// <summary>The end of that period.</summary>
    public DateTime? ReconciledPeriodTo { get; set; }

    /// <summary>
    /// How much this change moved that period's recorded card sales
    /// (<see cref="ResultingCountedAmount"/> minus <see cref="PreviousCountedAmount"/>).
    /// </summary>
    public decimal? ReconciledPeriodDifference { get; set; }
}
