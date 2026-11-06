using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILatestNayaxSalesStore"/> (issue #187, extended by issue
/// #1103). It lives in Inventory.Infrastructure, following the same pattern as
/// <see cref="EfMachineStockEventStore"/>: <see cref="AppDbContext"/> and the
/// <see cref="NayaxSales"/> persistence model it depends on moved there in issue #307, and this
/// adapter family followed them in issue #309 (Persistence 8/8 of #153).
///
/// The import rules themselves are unchanged from the former private
/// <c>MachineService.SaveMachinesLastSalesAsync</c>: deduplication by the remote
/// <c>TransactionID</c>, product matching through the Domain <see cref="ProductMatcher"/> (issue
/// #301 removed the <c>NayaxProductMatcher</c> wrapper these calls went through; the catalogue is
/// projected onto candidates once per batch and the ID/name matching semantics, candidate selection
/// and business scoping are unchanged), the
/// settlement-value completed/cancelled default, historical costing through the Application
/// <see cref="ICostSale"/> use case (issue #297), and the baseline-cutoff-gated
/// <see cref="IRebuildProductCost"/> replay. An already stored transaction is only enriched
/// where it is still missing its product match or status, so an imported status or cost is never
/// overwritten - and neither is its stored instant, which is what makes re-reading the rolling
/// last-sales window unable to shift a sale a second time (issue #380).
///
/// <para><strong>Issue #1103 made this adapter the one place both sales paths save through, and
/// changed three things.</strong> First, the <c>SettlementValue &gt; 0</c> status default is now the
/// rule for a sale that <em>supplies no status</em>, which is every <c>lastSales</c> item; a Nayax SQS
/// message supplies its own <c>Transaction Status ID</c> and that is saved exactly as given, so a
/// declined transaction that charged money is stored as declined instead of as a completed sale that
/// consumes stock. Second, what has to be replayed is written down: the same save that stores a sale
/// records a <see cref="PendingInventoryCostRebuild"/> for every product it affected, and
/// <see cref="RebuildInventoryCostsAsync"/> drains those rows and removes each one in the same
/// transaction as the costs it rebuilt - and only while the row still names the instant that was
/// replayed, because the replay itself runs outside the write lock and a writer that recorded an
/// earlier sale meanwhile has recorded work no acknowledgement of the older claim may clear. Before
/// that, a failed replay was simply forgotten - the sale
/// was already stored, so the next import reported no affected products and never retried - and a
/// queue reader deleting a message on "already stored" would have lost it for good. Third,
/// <see cref="PersistSqsSaleAsync"/> saves one message inside one <c>BEGIN IMMEDIATE</c> transaction;
/// see <c>EfLatestNayaxSalesStore.Sqs.cs</c>.</para>
///
/// The one identity rule that did change is where a payload item's <c>NayaxProductID</c> is resolved
/// (issue #745, parent #329 decision A2b): through the recorded provider id, by
/// <see cref="Inventory.Application.Nayax.INayaxProductLinkStore"/>, instead of by comparing it with
/// <c>Product.Id</c>. For data that predates it nothing changes - issue #732's backfill recorded
/// every product's Nayax id as the one it was keyed by, and issue #759's migration copied that onto
/// <c>Product.ExternalProductId</c> - and a Nayax id recorded on no product is an unknown product,
/// exactly as an unmatched id already was.
///
/// Issue #758 (A2c) made that answer a stored fact rather than something every later reader redoes:
/// the matched local product is written to <c>NayaxSales.ProductId</c>, the payload's own
/// <c>NayaxProductID</c> is written to <c>NayaxSales.NayaxProductId</c> and marked raw, and the
/// originating provider is recorded. The enrichment branch fills a stored sale's missing
/// association the same way - except for a sale issue #758's migration marked conflicting, which
/// only an audited decision may resolve.
///
/// The one behavioural change issue #380 made here is which payload field the stored instant comes
/// from: <c>NayaxSales.MachineAuthorizationTime</c> is a persisted true UTC instant, so it is written
/// from the authoritative <c>AuthorizationDateTimeGMT</c> value through
/// <see cref="NayaxLastSalesReport.AuthorizationInstantUtc"/>, not from the identically named but
/// machine-local <c>MachineAuthorizationTime</c> payload field. The rebuild step is per product rather than all-or-nothing (issue #362): the sales
/// are saved first and never retried, so one product's unreplayable history must not discard the
/// other products' rebuilds, and the collected failures are reported afterwards instead.
/// </summary>
public sealed partial class EfLatestNayaxSalesStore : ILatestNayaxSalesStore
{
    /// <summary>
    /// The short codes <see cref="PendingInventoryCostRebuild.LastFailureCode"/> may hold. They are
    /// codes rather than the data-quality sentence on purpose: a failure message can name a product,
    /// a purchase or a figure, and this column is read by an operational view rather than by the
    /// person the real message is written for. The message itself still reaches the caller.
    /// </summary>
    internal static class PendingRebuildFailureCodes
    {
        /// <summary>The replay found a fatal data-quality issue in the product's history.</summary>
        public const string DataQuality = "data_quality";

        /// <summary>
        /// The product the row names could not be loaded. It should be unreachable - a sale's
        /// restricted foreign key stops its product being deleted - and is caught so one impossible
        /// row cannot stop every other product's replay.
        /// </summary>
        public const string ProductMissing = "product_missing";
    }

    private readonly AppDbContext _db;
    private readonly ICostSale _saleCosting;
    private readonly IRebuildProductCost _inventoryCostRebuild;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IClock _clock;

    public EfLatestNayaxSalesStore(
        AppDbContext db,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild,
        INayaxProductLinkStore productLinks,
        IClock clock)
    {
        _db = db;
        _saleCosting = saleCosting;
        _inventoryCostRebuild = inventoryCostRebuild;
        _productLinks = productLinks;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<LatestNayaxSalesPersistResult> PersistLatestSalesAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sales);

        var affected = new Dictionary<long, DateTime>();
        if (sales.Count == 0)
            return new LatestNayaxSalesPersistResult(affected);

        // One BEGIN IMMEDIATE transaction around the reads and the writes, for the reason
        // PersistSqsSaleAsync takes one (issue #1103): this path reads "is this transaction stored?"
        // and then inserts, and the queue reader does the same thing at the same time. Without the
        // write lock both read "not stored" and the loser's whole save is refused by the unique index
        // on (BusinessId, TransactionID) - a failed Dashboard refresh over a sale the other path had
        // just stored. With it the second writer waits, then reads the first one's committed state and
        // takes the enrich path, which is a deliberate outcome instead of a lock error. The batch was
        // already one save, so what changes is where the reads sit, not how much is committed at once.
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var context = await ReadMatchingContextAsync(sales, cancellationToken);

        foreach (var sale in sales)
        {
            // No lastSales item supplies a status: the documented response for
            // GET /v1/machines/{MachineID}/lastSales has no status field at all, so every row this
            // path inserts takes the settlement-value rule it always has. Passing null here, rather
            // than reading a field that is always null, is what makes that true by construction.
            await ApplySaleAsync(
                sale,
                suppliedStatusId: null,
                provenance: null,
                context,
                affected,
                cancellationToken);
        }

        // In the same save as the sales, which is what makes "the sale is stored" and "its replay is
        // remembered" one fact rather than two that can disagree after a crash.
        await RecordPendingRebuildsAsync(affected, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new LatestNayaxSalesPersistResult(affected);
    }

    /// <inheritdoc />
    public async Task<InventoryCostRebuildDrainResult> RebuildInventoryCostsAsync(
        CancellationToken cancellationToken)
    {
        // Oldest work first, so a product that has been waiting since yesterday is not starved by
        // today's sales. Read as plain values rather than as tracked rows, deliberately: a replay
        // takes as long as the product's history, and a row tracked across it is both a stale
        // FromInstantUtc that a later SaveChanges could write back over a newer one and shared state
        // two drains of the same business can stage changes to. What makes an acknowledgement safe is
        // the write-locked re-read in AcknowledgeAsync, never this read.
        var pending = await _db.PendingInventoryCostRebuilds.AsNoTracking()
            .OrderBy(row => row.CreatedAtUtc)
            .ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.ProductId, row.FromInstantUtc })
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return new InventoryCostRebuildDrainResult([], []);

        var claims = pending.ConvertAll(
            row => new PendingRebuildClaim(row.Id, row.ProductId, row.FromInstantUtc));

        var productIds = claims.ConvertAll(claim => claim.ProductId);
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);

        // One product's unreplayable history used to discard the whole batch's staged rebuilds,
        // leaving every other product's costing position stale until its next sale, because the
        // sales themselves were already saved and were never reconsidered (issue #362). A fatal
        // rebuild stages nothing, so the products that did rebuild are saved and the failures are
        // reported afterwards - never swallowed.
        var failures = new List<string>();
        var stillPending = new List<long>();
        var attemptedAtUtc = _clock.UtcNow;

        foreach (var claim in claims)
        {
            if (!baselines.TryGetValue(claim.ProductId, out var cutoff) || claim.FromInstantUtc <= cutoff)
            {
                // Nothing to replay: there is no transition baseline for this product, or the
                // instant is at or before the cutoff the baseline fixed. That is the baseline doing
                // its job, so the work is done rather than failed and the row goes - and goes only
                // while it still names the instant that was judged, because a writer that has since
                // recorded an earlier sale has recorded work this claim says nothing about.
                if (!await AcknowledgeAsync(claim, cancellationToken))
                    stillPending.Add(claim.ProductId);

                continue;
            }

            try
            {
                await _inventoryCostRebuild.RebuildAsync(
                    claim.ProductId, claim.FromInstantUtc, cancellationToken: cancellationToken);
            }
            catch (InventoryCostDataQualityException ex)
            {
                await RecordFailedAttemptAsync(
                    claim, PendingRebuildFailureCodes.DataQuality, attemptedAtUtc, cancellationToken);
                stillPending.Add(claim.ProductId);
                failures.Add($"Product {claim.ProductId}: {ex.Message}");
                continue;
            }
            catch (InvalidOperationException ex)
            {
                await RecordFailedAttemptAsync(
                    claim, PendingRebuildFailureCodes.ProductMissing, attemptedAtUtc, cancellationToken);
                stillPending.Add(claim.ProductId);
                failures.Add($"Product {claim.ProductId}: {ex.Message}");
                continue;
            }

            if (!await AcknowledgeAsync(claim, cancellationToken))
            {
                // The work moved while this replay was running, so the replay that just finished is
                // not the work that is pending now: nothing of it was saved, and the product is
                // reported as still pending because it is. The next drain replays from the newer,
                // earlier instant - which is the whole point of the row having survived.
                stillPending.Add(claim.ProductId);
            }
        }

        return new InventoryCostRebuildDrainResult(stillPending, failures);
    }

    /// <summary>
    /// One row of pending work as this drain read it. The instant is the <em>claim</em>: the
    /// acknowledgement removes the row only while the row still holds it, which is what makes
    /// replaying outside the write lock safe (see <see cref="AcknowledgeAsync"/>).
    /// </summary>
    private readonly record struct PendingRebuildClaim(long Id, long ProductId, DateTime FromInstantUtc);

    /// <summary>
    /// Removes the row a claim was read from and saves whatever the replay staged, in one
    /// <c>BEGIN IMMEDIATE</c> transaction - but only while the row still holds the instant that was
    /// replayed from. Answers whether it did.
    ///
    /// <para><strong>Why the re-read decides, and not the read the replay started from.</strong> The
    /// replay is deliberately <em>outside</em> the write lock: it is a long chronological
    /// read-and-write over a product's whole history, and holding SQLite's one write lock for it would
    /// block every interactive request. Another writer - the queue reader, the request-driven sync, or
    /// a second drain - can therefore record an earlier sale for the same product and commit while
    /// this replay is running, and recording one only ever moves <c>FromInstantUtc</c> earlier.
    /// Removing the row by key would throw that newly arrived work away for good: the row would say
    /// "replay from the earlier instant", only the later span was replayed, and nothing would be left
    /// to say so. So the row is read again <em>inside</em> the lock - where no other writer can commit
    /// between the read and the removal - and removed only while it still says what was replayed.</para>
    ///
    /// <para><strong>A claim that has gone stale commits nothing at all.</strong> The costs the replay
    /// staged were computed before the earlier sale existed, so saving them would let a stale replay
    /// overwrite what the drain replaying from the earlier instant writes. The staged changes are
    /// therefore discarded rather than saved: nothing of this product's replay is persisted, and the
    /// row - whichever instant it now holds - stays the standing record of what still has to be
    /// replayed.</para>
    /// </summary>
    private async Task<bool> AcknowledgeAsync(
        PendingRebuildClaim claim, CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var current = await _db.PendingInventoryCostRebuilds
            .FirstOrDefaultAsync(
                row => row.Id == claim.Id && row.FromInstantUtc == claim.FromInstantUtc,
                cancellationToken);

        if (current is null)
        {
            // Nothing is written and nothing is removed, and the staged costs go with it: this
            // context belongs to one message's scope or one synchronization's request, and what it
            // holds at this point is this product's abandoned rebuild. The sales and the earlier
            // products' costs are already committed by their own saves.
            _db.ChangeTracker.Clear();
            return false;
        }

        _db.PendingInventoryCostRebuilds.Remove(current);

        // The removal and the costs the replay staged in one SaveChanges - and therefore in the one
        // transaction this method opened - so "the costs are rebuilt" and "there is nothing left to
        // rebuild" can never disagree (AGENTS.md § Inventory and historical costing invariants).
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Records one failed attempt on the row a claim was read from: the attempt count, the instant and
    /// the short failure code, and never its <c>FromInstantUtc</c> - another writer may have moved
    /// that earlier while the replay was failing, and operational evidence about an attempt must not
    /// carry a stale instant back over newly recorded work. A row that is no longer there is left
    /// alone, because the work it described has been acknowledged by somebody else.
    /// </summary>
    private async Task RecordFailedAttemptAsync(
        PendingRebuildClaim claim,
        string failureCode,
        DateTime attemptedAtUtc,
        CancellationToken cancellationToken)
    {
        // A fatal rebuild stages nothing at all before it throws (issue #362), so there is nothing of
        // it to keep; anything else it left behind describes a rebuild that did not happen, and this
        // save is about the attempt and nothing else.
        _db.ChangeTracker.Clear();

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var row = await _db.PendingInventoryCostRebuilds
            .FirstOrDefaultAsync(candidate => candidate.Id == claim.Id, cancellationToken);

        if (row is null)
            return;

        row.AttemptCount++;
        row.LastAttemptAtUtc = attemptedAtUtc;
        row.LastFailureCode = failureCode;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything the per-sale matching needs, read once: the product candidates, the batch's
    /// provider-id resolutions, and the sales whose product association may not be settled
    /// automatically.
    /// </summary>
    private readonly record struct MatchingContext(
        List<ProductMatchCandidate> Candidates,
        IReadOnlyDictionary<long, long> LocalProductIdByNayaxId,
        HashSet<long> ConflictingSaleIds);

    private async Task<MatchingContext> ReadMatchingContextAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken)
    {
        var candidates = (await _db.Products.AsNoTracking().ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

        // The batch's Nayax product ids are resolved to local product ids once, through the one
        // lookup over the recorded provider ids (issue #745, parent #329 decision A2b),
        // rather than compared with Product.Id - which only ever worked because the local key *is*
        // the Nayax id. Read after the catalogue read and before the per-sale work, in sequence with
        // it: both land on the caller's own scoped AppDbContext, which supports one operation at a
        // time (docs/architecture.md § Concurrency inside one request).
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            sales
                .Where(sale => sale.NayaxProductId.HasValue)
                .Select(sale => sale.NayaxProductId!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        // The sales whose two candidate products disagreed when issue #758 resolved the existing
        // history. A conflicting sale is never resolved automatically - only an audited decision
        // names its product - so the enrichment below leaves one alone however well the fresh
        // payload would resolve. Expected to be empty: it is one tenant-filtered read of a table
        // the migration only writes to when it finds an ambiguity it must not settle.
        var conflictingSaleIds = (await _db.NayaxSaleProductConflicts.AsNoTracking()
                .Select(conflict => conflict.NayaxSaleId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return new MatchingContext(candidates, localProductIdByNayaxId, conflictingSaleIds);
    }

    /// <summary>
    /// The SQS provenance of a sale this save is <em>creating</em>, or <see langword="null"/> for the
    /// <c>lastSales</c> path and for any enrichment. It is written on the insert and never afterwards:
    /// a transaction the request-driven sync stored first still says truthfully that it was not
    /// created from the queue.
    /// </summary>
    private readonly record struct SqsProvenance(string MessageId, DateTime ReceivedAtUtc);

    /// <summary>What applying one payload item did.</summary>
    /// <param name="Stored">
    /// Whether the transaction is now stored at all. <see langword="false"/> only for a payload item
    /// carrying no authoritative instant, which is never imported at a guessed time.
    /// </param>
    /// <param name="Inserted">Whether <em>this</em> application created the row.</param>
    /// <param name="CompletedSaleProductId">
    /// The local product the stored row consumes costing inventory for, or <see langword="null"/> when
    /// the row is not a completed sale or resolved no product.
    ///
    /// <para>It is reported for an <em>already stored</em> row as well as for a freshly inserted one,
    /// and that is what makes a redelivered queue message wait: the message's own transaction is
    /// already saved, so without this the reader would see "nothing affected" and delete the message
    /// while that product's replay was still failing. It is deliberately <em>not</em> what
    /// <see cref="RecordPendingRebuildsAsync"/> records from - a re-read of the rolling last-sales
    /// window would otherwise queue a replay for every sale in it on every synchronization.</para>
    /// </param>
    private readonly record struct SaleApplication(
        bool Stored, bool Inserted, long? CompletedSaleProductId);

    /// <summary>
    /// Stores or enriches one sale and, when it is a completed sale against a resolved product,
    /// contributes that product's earliest affected instant to <paramref name="affected"/>.
    ///
    /// <para><strong>The status rule is here and nowhere else.</strong>
    /// <paramref name="suppliedStatusId"/> is the whole difference between the two paths: a supplied
    /// status is saved exactly as given on an insert, and the <c>SettlementValue &gt; 0</c> rule
    /// applies only when none is supplied. On an enrichment a supplied status fills the column only
    /// while the stored one is <c>NULL</c> - exactly as the amount-derived fallback already did - so
    /// neither path ever overwrites a status the other decided. A later difference between the two is
    /// issue #1104's comparison to record, not this store's to resolve.</para>
    /// </summary>
    private async Task<SaleApplication> ApplySaleAsync(
        NayaxLastSalesReport sale,
        int? suppliedStatusId,
        SqsProvenance? provenance,
        MatchingContext context,
        Dictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        var matchedProductId = ProductMatcher.MatchLinked(
            context.Candidates,
            LocalProductId(sale.NayaxProductId, context.LocalProductIdByNayaxId),
            sale.ProductName);

        var existing = await _db.NayaxSales
            .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, cancellationToken);

        return existing is null
            ? await InsertSaleAsync(sale, suppliedStatusId, provenance, matchedProductId, affected, cancellationToken)
            : await EnrichExistingSaleAsync(
                existing, sale, suppliedStatusId, context, matchedProductId, affected, cancellationToken);
    }

    /// <summary>
    /// The insert half of <see cref="ApplySaleAsync"/>: a transaction not seen before.
    /// </summary>
    private async Task<SaleApplication> InsertSaleAsync(
        NayaxLastSalesReport sale,
        int? suppliedStatusId,
        SqsProvenance? provenance,
        long? matchedProductId,
        Dictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        // The sale instant is the payload's authoritative AuthorizationDateTimeGMT, normalized
        // once at the integration boundary (NayaxLastSalesReport.AuthorizationInstantUtc,
        // issue #380) - never the machine-local MachineAuthorizationTime wall clock, whose
        // ticks are up to eleven hours away from the instant they would be stored as. A payload
        // item carrying no authoritative instant is not imported at a guessed time; the rolling
        // last-sales window returns the transaction again on the next refresh.
        if (sale.AuthorizationInstantUtc is not { } authorizedUtc)
            return new SaleApplication(Stored: false, Inserted: false, CompletedSaleProductId: null);

        var added = new NayaxSales
        {
            TransactionID = sale.TransactionID,
            TransactionStatusId = suppliedStatusId ?? StatusFromSettlementValue(sale),
            MachineID = sale.MachineID,
            // The local product this synchronization matched, and separately the provider's
            // own product id exactly as the payload carried it (issue #758). The column that
            // used to hold either one now holds only the latter, and says so.
            ProductId = matchedProductId,
            Provider = ExternalProvider.Nayax,
            NayaxProductId = sale.NayaxProductId,
            ProviderProductIdIsRaw = true,
            MachineName = sale.MachineName,
            SettlementValue = sale.SettlementValue,
            PaymentMethod = sale.PaymentMethod,
            ProductName = sale.ProductName,
            NayaxProductCostPrice = sale.ProductCostPrice,
            MachineAuthorizationTime = authorizedUtc
        };

        if (provenance is { } sqs)
        {
            // On the same insert as the row, which is what "its provenance arrives with it"
            // means: there is no moment at which a queue-sourced sale exists without saying so.
            added.ReceivedViaSqsAtUtc = sqs.ReceivedAtUtc;
            added.SqsFirstMessageId = sqs.MessageId;
            added.SqsVerification = NayaxSqsVerification.Unverified;
        }

        _db.NayaxSales.Add(added);
        await _saleCosting.CostAsync(added, cancellationToken: cancellationToken);

        var insertedCompleted =
            NayaxTransactionStatusClassifier.IsCompletedSale(added.TransactionStatusId);

        if (insertedCompleted && matchedProductId is not null)
            RecordAffected(affected, matchedProductId.Value, added.MachineAuthorizationTime);

        return new SaleApplication(
            Stored: true,
            Inserted: true,
            CompletedSaleProductId: insertedCompleted ? matchedProductId : null);
    }

    /// <summary>
    /// The enrichment half of <see cref="ApplySaleAsync"/>: a transaction already stored, filled in
    /// only where it is still missing its product match or status.
    /// </summary>
    private async Task<SaleApplication> EnrichExistingSaleAsync(
        NayaxSales existing,
        NayaxLastSalesReport sale,
        int? suppliedStatusId,
        MatchingContext context,
        long? matchedProductId,
        Dictionary<long, DateTime> affected,
        CancellationToken cancellationToken)
    {
        var enriched = false;
        if (!existing.ProductId.HasValue && !context.ConflictingSaleIds.Contains(existing.Id))
        {
            var existingMatch = ProductMatcher.MatchLinked(
                context.Candidates,
                LocalProductId(sale.NayaxProductId, context.LocalProductIdByNayaxId),
                sale.ProductName ?? existing.ProductName);
            if (existingMatch is not null)
            {
                // The enrichment fills the association the stored row is still missing, and
                // records the provider's own id beside it with its provenance (issue #758).
                existing.ProductId = existingMatch.Value;
                existing.Provider = ExternalProvider.Nayax;
                existing.NayaxProductId = sale.NayaxProductId;
                existing.ProviderProductIdIsRaw = true;
                matchedProductId = existingMatch;
                enriched = true;
            }
        }

        if (!existing.TransactionStatusId.HasValue)
        {
            existing.TransactionStatusId = suppliedStatusId ?? StatusFromSettlementValue(sale);
            enriched = true;
        }

        if (enriched)
        {
            await _saleCosting.CostAsync(
                existing,
                cancellationToken: cancellationToken);
            if (NayaxTransactionStatusClassifier.IsCompletedSale(existing.TransactionStatusId))
            {
                // The association persisted on the row (issue #758) - never a match re-made
                // from the provider's product id or the reported name. A sale whose product a
                // writer already decided replays that product; an unresolved sale replays none,
                // which is what keeps a rename or a duplicate name out of cost history.
                matchedProductId ??= existing.ProductId;
                if (matchedProductId is not null)
                    RecordAffected(affected, matchedProductId.Value, existing.MachineAuthorizationTime);
            }
        }

        return new SaleApplication(
            Stored: true,
            Inserted: false,
            CompletedSaleProductId:
                NayaxTransactionStatusClassifier.IsCompletedSale(existing.TransactionStatusId)
                    ? existing.ProductId
                    : null);
    }

    /// <summary>
    /// The status a sale that supplies none takes: a positive settlement value is a completed sale
    /// and anything else is cancelled. It is the rule the <c>lastSales</c> path has always applied,
    /// and since issue #1103 it applies <em>only</em> there - a Nayax SQS message states its own
    /// <c>Transaction Status ID</c>, and a declined transaction that charged money is exactly the case
    /// the two would disagree about.
    /// </summary>
    private static int StatusFromSettlementValue(NayaxLastSalesReport sale) =>
        sale.SettlementValue > 0
            ? NayaxTransactionStatusIds.Completed
            : NayaxTransactionStatusIds.CancelledOrDeclined250;

    private static void RecordAffected(
        Dictionary<long, DateTime> affected, long productId, DateTime instantUtc)
    {
        if (!affected.TryGetValue(productId, out var existingAt) || instantUtc < existingAt)
            affected[productId] = instantUtc;
    }

    /// <summary>
    /// Stages one <see cref="PendingInventoryCostRebuild"/> per affected product, to be saved in the
    /// caller's own transaction alongside the sales that affected them.
    ///
    /// <para><strong>The instant only ever moves earlier.</strong> A product that already has a
    /// pending row keeps it and has its instant lowered when this batch's is earlier, because a
    /// replay from the earlier instant covers the later one and not the other way round. Moving it
    /// later would silently skip a sale's effect on cost. The unique index on
    /// <c>(BusinessId, ProductId)</c> is the database's backstop under this read.</para>
    /// </summary>
    private async Task RecordPendingRebuildsAsync(
        IReadOnlyDictionary<long, DateTime> affected, CancellationToken cancellationToken)
    {
        if (affected.Count == 0)
            return;

        var productIds = affected.Keys.ToList();
        var existing = await _db.PendingInventoryCostRebuilds
            .Where(row => productIds.Contains(row.ProductId))
            .ToListAsync(cancellationToken);

        var createdAtUtc = _clock.UtcNow;

        foreach (var (productId, fromInstantUtc) in affected)
        {
            var row = existing.Find(candidate => candidate.ProductId == productId);
            if (row is null)
            {
                _db.PendingInventoryCostRebuilds.Add(new PendingInventoryCostRebuild
                {
                    ProductId = productId,
                    FromInstantUtc = fromInstantUtc,
                    CreatedAtUtc = createdAtUtc,

                    // BusinessId is deliberately left unset: BusinessOwnershipEnforcer stamps it from
                    // the resolved scope on SaveChanges, exactly as it does for every other
                    // tenant-owned insert, so this adapter never assigns an owner itself.
                });
                continue;
            }

            if (fromInstantUtc < row.FromInstantUtc)
                row.FromInstantUtc = fromInstantUtc;
        }
    }

    /// <summary>
    /// The local product a payload item's <c>NayaxProductID</c> names, or <see langword="null"/>
    /// when this business has recorded that Nayax id on no product - an unknown product, handled exactly as an
    /// unmatched id already was: the sale is still imported, still visible and left for the name
    /// fallback and then the uncosted path, never attached to the product whose local id happens to
    /// equal the remote one (issue #745).
    /// </summary>
    private static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;
}
