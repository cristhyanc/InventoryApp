using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SaleTimestampRepair;

/// <summary>
/// Writes a previewed Nayax sale timestamp repair (issue #472), and only ever exactly that.
///
/// Everything happens in one transaction, and in this order deliberately: the stored plan is loaded,
/// the sales it names are re-read authoritatively, the plan is compared against that read, the
/// repaired instants and their audit rows are written, and the affected products' costing is
/// replayed. The read, the comparison and the write are one operation, so a sale imported, re-timed,
/// restatused, rematched or already repaired between the preview and the apply is refused rather
/// than repaired against a plan nobody approved.
///
/// <b>Nothing a caller submits is written.</b> The request carries a preview id and an explicit
/// confirmation; every instant, business date, source provenance and audit value comes from the plan
/// the preview stored. The plan is tenant-owned, so another business's preview id simply does not
/// exist for this caller, and it may be applied once and only before it expires.
///
/// <b>What it is allowed to change is one column.</b> A repair writes a sale's authorization instant
/// and nothing else - not its transaction id, amount, status, payment method or product mapping - and
/// it never adds or removes a sale, so no transaction can be duplicated and no physical stock
/// movement is repeated. A transaction an export carries that this business holds no sale for is a
/// missing sale and is reported by the preview, never created here.
///
/// <b>Costing is replayed from the earlier of each affected sale's old and new instant</b>
/// (<see cref="NayaxSaleTimestampRepairPolicy.EarliestAffectedInstant"/>), through the same
/// baseline-cutoff-gated <see cref="IRebuildProductCost.RebuildAsync"/> path the uploaded sales
/// import and the latest-sales synchronization use when they move or add a sale, so a re-dated sale
/// cannot leave stale COGS at its old position and the replay stays the one authoritative one.
///
/// <b>Failure semantics are all-or-nothing.</b> A product whose cost history cannot be replayed - an
/// incomplete opening position, an uncostable sale - aborts the whole apply: the transaction is
/// rolled back, no instant is repaired, no audit row is kept, the draft stays unapplied and the
/// operator is told which product failed. Nothing fabricates an opening cost to make the replay
/// succeed, and a failed replay is never reported as a successful repair (AGENTS.md § Inventory and
/// historical costing invariants). Recovery is to fix the product's cost history - the explicit
/// costing repair exists for that - and run this preview and apply again.
/// </summary>
public sealed class ApplyNayaxSaleTimestampRepair
{
    /// <summary>The caller-safe refusal when the named plan is not an applicable one.</summary>
    public const string UnknownPreviewMessage =
        "This sale timestamp repair preview does not exist. Run the preview again.";

    private readonly INayaxSaleTimestampRepairStore _store;
    private readonly IRebuildProductCost _rebuild;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IBusinessCalendar _calendar;
    private readonly IClock _clock;

    public ApplyNayaxSaleTimestampRepair(
        INayaxSaleTimestampRepairStore store,
        IRebuildProductCost rebuild,
        IAuthenticatedActorAccessor actors,
        IBusinessCalendar calendar,
        IClock clock)
    {
        _store = store;
        _rebuild = rebuild;
        _actors = actors;
        _calendar = calendar;
        _clock = clock;
    }

    public async Task<NayaxSaleTimestampRepairApplied> Handle(
        ApplyNayaxSaleTimestampRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Confirmed)
            throw new DomainValidationException(
                "Explicit confirmation is required to repair stored Nayax sale timestamps.");

        // Resolved before anything is read or written: a repair that cannot name who applied it is
        // not auditable, so an unidentifiable caller is refused rather than recorded as blank.
        var actor = _actors.GetCurrentActor();
        var identity = actor.Actor
            ?? throw new BusinessAccessDeniedException(
                actor.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var draft = await _store.FindDraftAsync(request.PreviewId, cancellationToken)
            ?? throw new DomainValidationException(UnknownPreviewMessage);
        var appliedAt = _clock.UtcNow;
        NayaxSaleTimestampRepairPolicy.EnsureDraftUsable(draft.AppliedAt, draft.ExpiresAt, appliedAt);

        var plan = JsonSerializer.Deserialize<NayaxSaleTimestampRepairPlan>(draft.PlanJson)
            ?? throw new DomainValidationException(UnknownPreviewMessage);
        var previewed = plan.Decisions.Select(decision => decision.Sale).ToList();
        var current = await NayaxSaleTimestampRepairSales.ReadAsync(
            _store,
            previewed.Select(sale => sale.TransactionId).ToList(),
            plan.ExaminedFromUtc,
            plan.ExaminedToUtc,
            cancellationToken);
        NayaxSaleTimestampRepairPolicy.EnsureStoredSalesUnchanged(previewed, current);

        var changes = plan.Decisions
            .Where(decision => decision.Outcome == NayaxSaleTimestampRepairOutcome.Repairable)
            .Select(ToChange)
            .ToList();

        IReadOnlyList<NayaxSaleTimestampRepairRecord> repairs = changes.Count == 0
            ? []
            : await _store.RepairAsync(
                new(request.PreviewId, appliedAt, identity.DirectoryTenantId, identity.ObjectId, changes),
                cancellationToken);
        _store.MarkDraftApplied(request.PreviewId, appliedAt);
        await _store.SaveChangesAsync(cancellationToken);

        var (productsRebuilt, recostedSales) = await RebuildAsync(plan, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(request.PreviewId, repairs.Count, productsRebuilt, recostedSales, repairs);
    }

    /// <summary>
    /// Replays each planned product from the instant the preview reported, which is already the
    /// earlier of its affected sales' old and new instants and already gated on the product's
    /// transition baseline cutoff - a sale at or before that cutoff is covered by the baseline, so
    /// replaying it would recost history the transition owns.
    /// </summary>
    private async Task<(int ProductsRebuilt, int RecostedSales)> RebuildAsync(
        NayaxSaleTimestampRepairPlan plan,
        CancellationToken cancellationToken)
    {
        var rebuilt = 0;
        var recosted = 0;
        foreach (var product in plan.AffectedProducts.Where(product => product.RebuildPlanned))
        {
            InventoryCostRebuildResult result;
            try
            {
                result = await _rebuild.RebuildAsync(
                    product.ProductId, product.RebuildFromUtc, cancellationToken: cancellationToken);
            }
            catch (InventoryCostDataQualityException exception)
            {
                // All-or-nothing: the transaction's disposal rolls back every repaired instant and
                // audit row, and the message says which product's history has to be fixed first. A
                // partly costed product is never left behind, and no opening cost is invented.
                throw new DomainValidationException(
                    $"Nothing was repaired: the inventory cost of {Describe(product)} could not be replayed from "
                        + $"{product.RebuildFromUtc:yyyy-MM-ddTHH:mm:ssZ}. {exception.Message}");
            }

            rebuilt++;
            recosted += result.RecostedSaleCount;
        }

        return (rebuilt, recosted);
    }

    private static string Describe(NayaxSaleTimestampRepairProduct product) =>
        string.IsNullOrWhiteSpace(product.ProductName)
            ? $"product {product.ProductId}"
            : $"{product.ProductName} (product {product.ProductId})";

    /// <summary>
    /// One repairable decision as the write and its audit row. The two business dates are
    /// recorded with the instants rather than derived later, because that is the movement the
    /// operator approved and it must stay readable exactly as it was applied.
    /// </summary>
    private NayaxSaleTimestampRepairChange ToChange(NayaxSaleTimestampDecision decision)
    {
        var repaired = decision.RepairedInstantUtc!.Value;
        return new(
            decision.Sale.TransactionId,
            decision.Sale.MachineId,
            decision.Sale.StoredInstantUtc,
            repaired,
            _calendar.ToBusinessDate(decision.Sale.StoredInstantUtc),
            _calendar.ToBusinessDate(repaired),
            decision.Evidence!.Source,
            decision.Evidence.Reference);
    }
}
