using Inventory.Domain.Nayax;

namespace Inventory.Application.SaleTimestampRepair;

// The Nayax sale timestamp repair's Preview/Apply contracts (issue #472), owned by the Application
// layer. The preview is the instrument: it says, per transaction, which stored instant source
// evidence would move and where to, what that does to each Sydney business day's revenue, which
// products' costing would be replayed and from when, and - against a fixed cutoff - how the result
// reconciles with the operator's own Nayax export totals. The apply carries nothing but the preview
// it confirms.

/// <summary>
/// Which supported sources a preview collects authoritative <c>AuthorizationDateTimeGMT</c> values
/// from, and the fixed-cutoff reconciliation window it reports.
/// </summary>
/// <param name="IncludeLatestSalesApiEvidence">
/// Read every machine's live rolling last-sales window through <c>INayaxLynxClient</c>. This is the
/// authoritative source for the transactions that window still returns, and nothing else.
/// </param>
/// <param name="Reconciliation">
/// The fixed-cutoff Sydney reconciliation window to report, or <c>null</c> for none. All three of
/// its values are required together, because a daily or weekly total is only comparable with an
/// export's when both sides exclude the sales authorized after the same instant.
/// </param>
public sealed record NayaxSaleTimestampRepairPreviewRequest(
    bool IncludeLatestSalesApiEvidence,
    NayaxSaleTimestampReconciliationRequest? Reconciliation = null);

/// <summary>
/// The fixed cutoff and inclusive Sydney business-date window a reconciliation is computed over.
/// </summary>
/// <param name="CutoffUtc">
/// The instant the compared export was taken at. A sale authorized after it is excluded from both
/// sides of the comparison and reported separately, because a later sale is not a discrepancy.
/// </param>
/// <param name="FromBusinessDate">The first inclusive <c>Australia/Sydney</c> business date.</param>
/// <param name="ToBusinessDate">The last inclusive <c>Australia/Sydney</c> business date.</param>
public sealed record NayaxSaleTimestampReconciliationRequest(
    DateTime CutoffUtc,
    DateTime FromBusinessDate,
    DateTime ToBusinessDate);

/// <summary>
/// One examined stored sale, with the instant and Sydney business date it holds now, the ones the
/// repair would give it, and the source that decided it. A row whose outcome is
/// <see cref="NayaxSaleTimestampRepairOutcome.Unresolved"/> keeps its stored values and states why.
/// </summary>
public sealed record NayaxSaleTimestampRepairRow(
    long TransactionId,
    long MachineId,
    string? MachineName,
    decimal SettlementValue,
    int? TransactionStatusId,
    bool CompletedSale,
    DateTime StoredInstantUtc,
    DateTime StoredBusinessDate,
    DateTime? RepairedInstantUtc,
    DateTime? RepairedBusinessDate,
    NayaxSaleTimestampRepairOutcome Outcome,
    NayaxSaleTimestampUnresolvedReason? UnresolvedReason,
    NayaxSaleTimestampEvidenceSource? EvidenceSource,
    string? EvidenceReference);

/// <summary>
/// Revenue a repair moves between <c>Australia/Sydney</c> business days: what leaves a day because a
/// completed sale is re-dated away from it, what arrives because one is re-dated into it, and the
/// net. Only completed sales move revenue; a pending, refunded, cancelled or unknown-status row is
/// re-dated too but contributes nothing here, and stays visible in the rows above.
/// </summary>
public sealed record NayaxSaleTimestampRevenueMovement(
    DateTime BusinessDate,
    decimal AmountLeaving,
    decimal AmountArriving,
    decimal NetMovement);

/// <summary>
/// A product whose costing the repair would replay, and the instant the replay starts from: the
/// earlier of the affected sales' old and new instants, so a sale moving later cannot leave stale
/// COGS behind at its old position.
/// </summary>
/// <param name="ProductId">The local product whose costing would be replayed.</param>
/// <param name="ProductName">Its catalogue name, for the operator reviewing the plan.</param>
/// <param name="RebuildFromUtc">The instant the replay starts from.</param>
/// <param name="TransitionCutoffAt">The product's transition baseline cutoff, when it has one.</param>
/// <param name="HasTransitionBaseline">
/// Whether the product has an inventory-cost transition baseline at all. Without one its costing is
/// not replayed by any write path in the application, so this repair does not invent one either.
/// </param>
/// <param name="RebuildPlanned">
/// Whether the apply will replay this product: only when it has a baseline and
/// <paramref name="RebuildFromUtc"/> falls after that baseline's cutoff, which is the same gate the
/// sales import and the latest-sales synchronization apply.
/// </param>
public sealed record NayaxSaleTimestampRepairProduct(
    long ProductId,
    string ProductName,
    DateTime RebuildFromUtc,
    bool HasTransitionBaseline,
    DateTime? TransitionCutoffAt,
    bool RebuildPlanned);

/// <summary>
/// One <c>Australia/Sydney</c> business day of the fixed-cutoff reconciliation: its completed-sale
/// revenue and count as the database holds it now, as it would read after the repair, and the part of
/// that a source actually verified.
/// </summary>
/// <param name="BusinessDate">The <c>Australia/Sydney</c> business date.</param>
/// <param name="CompletedCountBefore">Completed sales the day holds now, before the cutoff.</param>
/// <param name="CompletedSalesBefore">Their settled value.</param>
/// <param name="CompletedCountAfter">Completed sales the day would hold after the repair.</param>
/// <param name="CompletedSalesAfter">Their settled value.</param>
/// <param name="UnresolvedCount">
/// Completed sales on this day that no source covered. They do not move, so they sit on the same day
/// before and after.
/// </param>
/// <param name="UnresolvedAmount">Their settled value.</param>
/// <param name="SourceVerifiedCountAfter">
/// <paramref name="CompletedCountAfter"/> less the unresolved ones.
/// </param>
/// <param name="SourceVerifiedAfter">
/// <paramref name="CompletedSalesAfter"/> less <paramref name="UnresolvedAmount"/>: the figure that is
/// comparable with the source export, because it covers exactly the transactions the source accounted
/// for. A day whose unresolved amount is zero is fully source-verified.
/// </param>
public sealed record NayaxSaleTimestampReconciliationDay(
    DateTime BusinessDate,
    int CompletedCountBefore,
    decimal CompletedSalesBefore,
    int CompletedCountAfter,
    decimal CompletedSalesAfter,
    int UnresolvedCount,
    decimal UnresolvedAmount,
    int SourceVerifiedCountAfter,
    decimal SourceVerifiedAfter);

/// <summary>
/// The fixed-cutoff reconciliation. It answers the only comparison that is valid against a Nayax
/// export: the same transaction set, the same cutoff, the same Sydney business days.
///
/// Three figures keep a timestamp repair from being mistaken for a complete explanation of a
/// reported difference:
/// <list type="bullet">
///   <item><see cref="ExcludedAfterCutoffAmount"/> - completed sales the application holds that the
///   compared export was taken too early to contain. Not a discrepancy.</item>
///   <item><see cref="UnresolvedCompletedAmount"/> - completed sales inside the window that no source
///   covered, so this repair neither moves nor explains them. They stay exactly where they are.</item>
///   <item><see cref="MissingFromDatabaseAmount"/> - transactions the evidence export carries that
///   this business holds no sale for at all. Those are missing sales, not timestamp defects: they are
///   imported through the ordinary uploaded-export import, and a repair can never create one.</item>
/// </list>
/// <see cref="SourceVerifiedTotalAfter"/> plus <see cref="MissingFromDatabaseAmount"/> is therefore
/// what a source export's own period total has to equal once the repair is applied and the missing
/// sales are imported; <see cref="TotalAfter"/> on its own still carries the unresolved rows, and a
/// repair never creates or destroys revenue - it only moves it between days.
/// </summary>
public sealed record NayaxSaleTimestampReconciliation(
    DateTime CutoffUtc,
    DateTime FromBusinessDate,
    DateTime ToBusinessDate,
    IReadOnlyList<NayaxSaleTimestampReconciliationDay> Days,
    int CompletedCountBefore,
    decimal TotalBefore,
    int CompletedCountAfter,
    decimal TotalAfter,
    int SourceVerifiedCountAfter,
    decimal SourceVerifiedTotalAfter,
    int ExcludedAfterCutoffCount,
    decimal ExcludedAfterCutoffAmount,
    int UnresolvedCompletedCount,
    decimal UnresolvedCompletedAmount,
    int MissingFromDatabaseCount,
    decimal MissingFromDatabaseAmount);

/// <summary>
/// One transaction the evidence export carries that the caller's business holds no stored sale for.
/// Reported so a missing sale is never counted as a repaired one.
/// </summary>
public sealed record NayaxSaleTimestampMissingSale(
    long TransactionId,
    long MachineId,
    DateTime? AuthorizationInstantUtc,
    DateTime? AuthorizationBusinessDate,
    decimal SettlementValue,
    int? TransactionStatusId,
    bool CompletedSale,
    NayaxSaleTimestampEvidenceSource EvidenceSource);

/// <summary>
/// What applying the repair would do, computed from the sources named in the request and stored as a
/// draft so the apply reads the server's own plan rather than a caller's.
///
/// <see cref="PreviewId"/> is the value <see cref="ApplyNayaxSaleTimestampRepair"/> requires back. It
/// identifies this exact plan, is owned by the business that created it, may be applied once, and
/// expires; and the apply additionally re-reads the stored sales it names and refuses the plan if any
/// of them changed.
///
/// <see cref="ExaminedFromUtc"/> and <see cref="ExaminedToUtc"/> are the affected range, derived from
/// the evidence rather than assumed: the earliest and latest instant the sources cover, widened to
/// the stored instants of the sales they name. Every stored sale inside it is examined, which is why
/// a transaction no source covered appears as explicitly unresolved instead of silently absent.
/// </summary>
public sealed record NayaxSaleTimestampRepairPreview(
    Guid PreviewId,
    int EvidenceRecords,
    DateTime? ExaminedFromUtc,
    DateTime? ExaminedToUtc,
    int SalesExamined,
    int Repairable,
    int AlreadyCorrect,
    int Unresolved,
    IReadOnlyList<NayaxSaleTimestampRepairRow> Rows,
    IReadOnlyList<NayaxSaleTimestampRevenueMovement> RevenueMovement,
    IReadOnlyList<NayaxSaleTimestampRepairProduct> AffectedProducts,
    IReadOnlyList<NayaxSaleTimestampMissingSale> MissingFromDatabase,
    NayaxSaleTimestampReconciliation? Reconciliation,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>
/// The stored plan a preview persisted, and the only thing an apply writes from. It holds the
/// server's own decisions - never a caller-supplied instant, outcome or provenance - and the examined
/// range, so the apply re-reads exactly the set the preview decided over and notices a sale added
/// inside it.
/// </summary>
public sealed record NayaxSaleTimestampRepairPlan(
    Guid PreviewId,
    DateTime? ExaminedFromUtc,
    DateTime? ExaminedToUtc,
    IReadOnlyList<NayaxSaleTimestampDecision> Decisions,
    IReadOnlyList<NayaxSaleTimestampRepairProduct> AffectedProducts);

/// <summary>
/// The confirmation of a preview: its identifier and an explicit confirmation, and nothing else.
///
/// There is deliberately no transaction, instant, business date, provenance or count here. The apply
/// reads all of them from the stored plan inside its own transaction, so a tampered request can only
/// name a plan that does not exist, is not this business's, has already been applied, has expired, or
/// no longer matches the database.
/// </summary>
public sealed record ApplyNayaxSaleTimestampRepairRequest(Guid PreviewId, bool Confirmed);

/// <summary>One stored repair, as the apply and the history report it.</summary>
public sealed record NayaxSaleTimestampRepairRecord(
    int Id,
    long TransactionId,
    long MachineId,
    DateTime PreviousInstantUtc,
    DateTime RepairedInstantUtc,
    DateTime PreviousBusinessDate,
    DateTime RepairedBusinessDate,
    NayaxSaleTimestampEvidenceSource EvidenceSource,
    string EvidenceReference,
    Guid PreviewId,
    DateTime AppliedAt,
    string AppliedByDirectoryTenantId,
    string AppliedByObjectId);

/// <summary>
/// One sale's repair to write, with the audit facts recorded alongside it. The store writes exactly
/// these and touches no other column: the transaction id, settled amount, status and product mapping
/// of the sale are untouched, and no sale is ever added or removed.
/// </summary>
public sealed record NayaxSaleTimestampRepairChange(
    long TransactionId,
    long MachineId,
    DateTime PreviousInstantUtc,
    DateTime RepairedInstantUtc,
    DateTime PreviousBusinessDate,
    DateTime RepairedBusinessDate,
    NayaxSaleTimestampEvidenceSource EvidenceSource,
    string EvidenceReference);

/// <summary>One operator's confirmed application of one previewed plan.</summary>
public sealed record NayaxSaleTimestampRepairApplication(
    Guid PreviewId,
    DateTime AppliedAt,
    string AppliedByDirectoryTenantId,
    string AppliedByObjectId,
    IReadOnlyList<NayaxSaleTimestampRepairChange> Changes);

/// <summary>
/// What the apply actually wrote: the repairs it recorded and the costing replay that followed them.
///
/// A plan with nothing repairable applies cleanly and writes nothing, which is what makes confirming
/// an already-applied repair - a fresh preview over evidence that now agrees with the database - a
/// no-op rather than an error.
/// </summary>
public sealed record NayaxSaleTimestampRepairApplied(
    Guid PreviewId,
    int SalesRepaired,
    int ProductsRebuilt,
    int RecostedSales,
    IReadOnlyList<NayaxSaleTimestampRepairRecord> Repairs);
