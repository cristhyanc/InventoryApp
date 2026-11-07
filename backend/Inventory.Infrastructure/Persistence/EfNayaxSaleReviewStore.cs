using Inventory.Application.Nayax;
using Inventory.Application.NayaxSaleReview;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSaleReviewStore"/> (issue #1105): the review list,
/// the Dashboard summary and the business Owner's decision.
///
/// <para><strong>One write-locked transaction per decision, in a fixed order.</strong> The decision
/// takes SQLite's write lock (<c>BEGIN IMMEDIATE</c>) before it reads anything, then: re-reads the
/// sale and its latest evidence; refuses with 409, writing nothing, when either is not what the
/// request presented or the named evidence is not the latest of its kind; resolves the product (for
/// "Use Nayax's values"); applies the change; writes the audit row; and stages the pending
/// inventory-cost rebuild. The evidence write of issue #1104 takes the same lock, so a decision and a
/// new piece of evidence for the same sale are strictly ordered and whichever commits second sees the
/// other.</para>
///
/// <para><strong>Nothing here takes a business id.</strong> Every statement runs in the caller's own
/// resolved scope, so the central query filters find the rows - a sale or evidence id of another
/// business simply is not found - and <see cref="BusinessOwnershipEnforcer"/> stamps the audit row.
/// There is no <c>IgnoreQueryFilters</c>.</para>
/// </summary>
public sealed class EfNayaxSaleReviewStore : INayaxSaleReviewStore
{
    /// <summary>The 409 when the product Nayax names resolves to no product of this business.</summary>
    public const string ProductNotLinkedMessage =
        "Nayax's product isn't linked to a product in this business. Link it first, or choose another decision.";

    /// <summary>The 409 when the sale or its evidence changed since the page read it.</summary>
    public const string StaleMessage =
        "This sale changed since you opened it. Refresh the page and decide again.";

    /// <summary>The 409 when the named evidence is not this sale's latest of its kind.</summary>
    public const string StaleEvidenceMessage =
        "The evidence this decision names is not this sale's latest. Refresh the page and decide again.";

    private static readonly NayaxSqsVerification[] NeedsReviewStates =
    [
        NayaxSqsVerification.Conflict,
        NayaxSqsVerification.NeedsInvestigation,
    ];

    private static readonly NayaxSqsVerification[] DecidedStates =
    [
        NayaxSqsVerification.OwnerConfirmed,
        NayaxSqsVerification.OwnerCorrected,
        NayaxSqsVerification.OwnerExcluded,
    ];

    private readonly AppDbContext _db;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IClock _clock;

    public EfNayaxSaleReviewStore(AppDbContext db, INayaxProductLinkStore productLinks, IClock clock)
    {
        _db = db;
        _productLinks = productLinks;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<NayaxSaleReviewSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var states = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.SqsVerification == NayaxSqsVerification.Conflict
                || sale.SqsVerification == NayaxSqsVerification.NeedsInvestigation)
            .GroupBy(sale => sale.SqsVerification)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var conflicts = states.Where(x => x.State == NayaxSqsVerification.Conflict).Sum(x => x.Count);
        return new NayaxSaleReviewSummary(states.Sum(x => x.Count), conflicts);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The two views are paged separately, so a flagged sale is never pushed off the page by decided
    /// ones: the needs-review view holds exactly the sales <see cref="SummaryAsync"/> counts, and every
    /// one of them is reachable by paging.
    /// </remarks>
    public async Task<NayaxSaleReviewPage> ListAsync(NayaxSaleReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var states = query.View switch
        {
            NayaxSaleReviewView.NeedsReview => NeedsReviewStates,
            NayaxSaleReviewView.Decided => DecidedStates,
            _ => throw new DomainValidationException("Choose which Nayax sales to list."),
        };

        if (query.PageIndex < 0)
        {
            throw new DomainValidationException("The page can't be negative.");
        }

        if (query.PageSize is < 1 or > NayaxSaleReviewQuery.MaxPageSize)
        {
            throw new DomainValidationException(
                $"A page lists between 1 and {NayaxSaleReviewQuery.MaxPageSize} sales.");
        }

        var inView = _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.SqsVerification != null && states.Contains(sale.SqsVerification.Value));

        var total = await inView.CountAsync(cancellationToken).ConfigureAwait(false);

        // Past the last page there is nothing to read, and a page index that large could not be
        // turned into a row offset without overflowing.
        var skip = (long)query.PageIndex * query.PageSize;
        if (skip >= total)
        {
            return new NayaxSaleReviewPage([], total, query.PageIndex, query.PageSize);
        }

        var sales = await inView
            .OrderByDescending(sale => sale.MachineAuthorizationTime)
            .ThenByDescending(sale => sale.Id)
            .Skip((int)skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NayaxSaleReviewPage(
            await ItemsAsync(sales, cancellationToken).ConfigureAwait(false),
            total,
            query.PageIndex,
            query.PageSize);
    }

    private async Task<IReadOnlyList<NayaxSaleReviewItem>> ItemsAsync(
        List<NayaxSales> sales, CancellationToken cancellationToken)
    {
        if (sales.Count == 0)
        {
            return [];
        }

        var saleIds = sales.ConvertAll(sale => sale.Id);
        var evidence = (await _db.NayaxSaleProviderEvidences.AsNoTracking()
                .Where(row => saleIds.Contains(row.NayaxSaleId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(row => row.NayaxSaleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Id).ToList());
        var audits = (await _db.NayaxSaleOwnerDecisionAudits.AsNoTracking()
                .Where(row => saleIds.Contains(row.NayaxSaleId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(row => row.NayaxSaleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Id).ToList());

        return sales.ConvertAll(sale =>
        {
            var rows = evidence.GetValueOrDefault(sale.Id) ?? [];
            var latest = rows.FirstOrDefault();
            var latestComparison = rows.Find(row => row.Kind == NayaxSaleProviderEvidenceKind.Comparison);
            var latestAbsence = rows.Find(row => row.Kind == NayaxSaleProviderEvidenceKind.Absence);
            var state = sale.SqsVerification!.Value;

            return new NayaxSaleReviewItem(
                sale.Id,
                sale.TransactionID,
                sale.MachineID,
                sale.MachineName,
                sale.SettlementValue,
                sale.MachineAuthorizationTime,
                sale.ReceivedViaSqsAtUtc,
                state,
                NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, state),
                sale.NayaxProductId,
                sale.ProductId,
                sale.ProductName,
                latest?.Id,
                latestComparison is null ? null : ToEvidence(latestComparison),
                latestAbsence is null ? null : ToEvidence(latestAbsence),
                (audits.GetValueOrDefault(sale.Id) ?? []).ConvertAll(ToHistory),
                Available(state, latest, latestComparison));
        });
    }

    /// <summary>
    /// The decisions the table allows, each with the evidence row it would rely on: a conflict
    /// resolution relies on the latest comparison, and the other two on the latest evidence of either
    /// kind.
    /// </summary>
    private static List<NayaxSaleAvailableDecision> Available(
        NayaxSqsVerification state,
        NayaxSaleProviderEvidence? latest,
        NayaxSaleProviderEvidence? latestComparison)
    {
        var available = new List<NayaxSaleAvailableDecision>();
        foreach (var decision in NayaxSaleOwnerDecisionPolicy.Available(state, latest?.Kind)
                     .Concat(NayaxSaleOwnerDecisionPolicy.Available(state, latestComparison?.Kind))
                     .Distinct()
                     .Order())
        {
            var relied = decision is NayaxSaleOwnerDecision.UseNayaxValues or NayaxSaleOwnerDecision.KeepSqsValues
                ? latestComparison
                : latest;
            if (relied is not null)
            {
                available.Add(new NayaxSaleAvailableDecision(decision, relied.Id));
            }
        }

        return available;
    }

    /// <inheritdoc />
    public async Task<NayaxSaleDecisionApplied?> DecideAsync(
        NayaxSaleDecisionRequest request, NayaxSaleDecisionActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // Before the reads: a deferred transaction would decide from a state another writer could
        // change before this one wrote, which is exactly the race the lock exists to prevent.
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // 1. Re-read the sale and its latest evidence, authoritatively.
        var sale = await AuthoritativeSaleAsync(request.SaleId, cancellationToken).ConfigureAwait(false);
        if (sale?.SqsVerification is not { } current)
        {
            return null;
        }

        // The sale's own row key, which evidence rows name as their NayaxSaleId.
        var saleKey = sale.Id;

        var latestId = await _db.NayaxSaleProviderEvidences.AsNoTracking()
            .Where(row => row.NayaxSaleId == saleKey)
            .OrderByDescending(row => row.Id)
            .Select(row => (long?)row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2. Refuse, writing nothing, when the page decided from a sale that has since changed.
        if (current != request.PresentedState || latestId != request.PresentedLatestEvidenceId)
        {
            throw new DomainConflictException(StaleMessage);
        }

        var named = await _db.NayaxSaleProviderEvidences.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.EvidenceId && row.NayaxSaleId == saleKey, cancellationToken)
            .ConfigureAwait(false);
        var latestOfKindId = named is null
            ? null
            : await _db.NayaxSaleProviderEvidences.AsNoTracking()
                .Where(row => row.NayaxSaleId == saleKey && row.Kind == named.Kind)
                .OrderByDescending(row => row.Id)
                .Select(row => (long?)row.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        if (named is null || latestOfKindId != named.Id)
        {
            throw new DomainConflictException(StaleEvidenceMessage);
        }

        var outcome = NayaxSaleOwnerDecisionPolicy.Evaluate(current, request.Decision, named.Kind, request.Reason);
        if (!outcome.Allowed)
        {
            throw new DomainValidationException(RefusalMessage(outcome.Refusal!.Value));
        }

        var before = NayaxSaleOwnerDecisionAuditing.Snapshot(sale);

        // 3. and 4. Resolve the product and apply.
        if (request.Decision is NayaxSaleOwnerDecision.UseNayaxValues)
        {
            await ApplyNayaxValuesAsync(sale, named, cancellationToken).ConfigureAwait(false);
        }

        sale.SqsVerification = outcome.ResultingState;

        // 5. The audit row, in the same transaction as the change.
        var audit = await NayaxSaleOwnerDecisionAuditing
            .NewRowAsync(_db, sale, before, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        audit.Kind = NayaxSaleOwnerDecisionAuditKind.Decision;
        audit.Decision = request.Decision;
        audit.EvidenceId = named.Id;
        audit.Reason = request.Reason?.Trim() is { Length: > 0 } reason ? reason : null;
        audit.ActorDirectoryTenantId = actor.DirectoryTenantId;
        audit.ActorObjectId = actor.ObjectId;
        _db.NayaxSaleOwnerDecisionAudits.Add(audit);

        // 6. The pending rebuild: the old product and the new one, from the earlier instant, whenever
        // whether the sale counts or any value costing reads has changed.
        await StageRebuildAsync(sale, before, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new NayaxSaleDecisionApplied(sale.Id, outcome.ResultingState!.Value, audit.Id);
    }

    /// <summary>
    /// "Use Nayax's values": the stored machine, amount, instant and product are replaced from the
    /// evidence. The local product is resolved through the same tenant-scoped resolver a new sale uses
    /// (<see cref="NayaxSaleProductResolver"/>); a Nayax product id is never written to
    /// <c>ProductId</c>. The raw id, its raw flag and the local product change together.
    /// </summary>
    private async Task ApplyNayaxValuesAsync(
        NayaxSales sale, NayaxSaleProviderEvidence evidence, CancellationToken cancellationToken)
    {
        if (evidence.ProviderNayaxProductId is null && string.IsNullOrWhiteSpace(evidence.ProviderProductName))
        {
            throw new DomainConflictException(ProductNotLinkedMessage);
        }

        var productId = await NayaxSaleProductResolver
            .ResolveAsync(_db, _productLinks, evidence.ProviderNayaxProductId, evidence.ProviderProductName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DomainConflictException(ProductNotLinkedMessage);

        if (evidence.ProviderMachineId is { } machineId)
        {
            sale.MachineID = machineId;
        }

        if (evidence.ProviderSettlementValue is { } amount)
        {
            sale.SettlementValue = amount;
        }

        if (evidence.ProviderAuthorizationInstantUtc is { } instant)
        {
            sale.MachineAuthorizationTime = instant;
        }

        sale.NayaxProductId = evidence.ProviderNayaxProductId;
        sale.ProviderProductIdIsRaw = true;
        sale.ProductId = productId;
        if (!string.IsNullOrWhiteSpace(evidence.ProviderProductName))
        {
            sale.ProductName = evidence.ProviderProductName;
        }
    }

    private async Task StageRebuildAsync(
        NayaxSales sale, NayaxSaleOwnerDecisionAuditing.SaleSnapshot before, CancellationToken cancellationToken)
    {
        var after = NayaxSaleOwnerDecisionAuditing.Snapshot(sale);
        var countingChanged = NayaxSaleCountingPolicy.ChangesCounting(before.State, after.State);
        var valuesChanged = before.MachineId != after.MachineId
            || before.SettlementValue != after.SettlementValue
            || before.AuthorizationInstantUtc != after.AuthorizationInstantUtc
            || before.ProductId != after.ProductId;

        if (!countingChanged && !valuesChanged)
        {
            return;
        }

        var from = before.AuthorizationInstantUtc < after.AuthorizationInstantUtc
            ? before.AuthorizationInstantUtc
            : after.AuthorizationInstantUtc;
        var affected = new Dictionary<long, DateTime>();
        PendingInventoryCostRebuildRecorder.Add(affected, before.ProductId, from);
        PendingInventoryCostRebuildRecorder.Add(affected, after.ProductId, from);

        await PendingInventoryCostRebuildRecorder
            .StageAsync(_db, affected, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RefusalMessage(NayaxSaleOwnerDecisionRefusal refusal) => refusal switch
    {
        NayaxSaleOwnerDecisionRefusal.ReasonRequired => "Give a reason for this decision.",
        NayaxSaleOwnerDecisionRefusal.ReasonTooLong =>
            $"The reason can be at most {NayaxSaleOwnerDecisionPolicy.MaxReasonLength} characters.",
        NayaxSaleOwnerDecisionRefusal.NotAllowedForEvidence =>
            "This decision needs Nayax's own record of the sale, and the evidence named has none.",
        _ => "This decision isn't available for this sale.",
    };

    /// <summary>
    /// The sale as committed now, inside the write lock: the tenant-filtered tracking query, then a
    /// reload, because the query alone answers with whatever instance this context already tracks.
    /// </summary>
    private async Task<NayaxSales?> AuthoritativeSaleAsync(long saleId, CancellationToken cancellationToken)
    {
        var sale = await _db.NayaxSales
            .FirstOrDefaultAsync(row => row.Id == saleId, cancellationToken)
            .ConfigureAwait(false);
        if (sale is null)
        {
            return null;
        }

        var entry = _db.Entry(sale);
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return entry.State is EntityState.Detached ? null : sale;
    }

    private static NayaxSaleReviewEvidence ToEvidence(NayaxSaleProviderEvidence row) => new(
        row.Id,
        row.Kind,
        row.ObservedAtUtc,
        row.ResultingState,
        row.ProviderMachineId,
        row.ProviderSettlementValue,
        row.ProviderAuthorizationInstantUtc,
        row.ProviderCurrencyCode,
        row.ProviderNayaxProductId,
        row.ProviderProductName,
        row.MachineComparison,
        row.AmountComparison,
        row.InstantComparison,
        row.ProductComparison,
        row.ReadMachineId,
        row.ReturnedItemCount,
        row.ReturnedEarliestInstantUtc,
        row.ReturnedLatestInstantUtc);

    private static NayaxSaleReviewHistoryEntry ToHistory(NayaxSaleOwnerDecisionAudit row) => new(
        row.Id,
        row.Kind == NayaxSaleOwnerDecisionAuditKind.Superseded,
        row.Decision,
        row.EvidenceId,
        row.Reason,
        row.OccurredAtUtc,
        row.PreviousState,
        row.ResultingState,
        row.ReconciledPeriodFrom,
        row.ReconciledPeriodTo,
        row.ReconciledPeriodDifference);
}
