using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCollectorMatchStore"/> (issue #941, parent #817):
/// the writes the collector's match proposals make.
///
/// <para><strong>One platform table, a listing move, and four columns of one tenant-owned row.</strong>
/// It writes <c>ProductMatchSuggestions</c>, re-points the <c>SupplierListings</c> held under a matched
/// waiting key onto the master product, and sets the status, the master product, the match source and
/// the instant on the <c>ProductPriceTracking</c> rows that carry that key and are still awaiting a
/// match. Nothing else is written, and the enforcer is what says so rather than this class: the one
/// <c>SaveChanges</c> runs inside <c>AppDbContext.BeginPriceCatalogueMatching</c>, whose window accepts
/// exactly those four columns on exactly that entity type and refuses every other tenant-owned write
/// (<c>BusinessOwnershipEnforcer</c>).</para>
///
/// <para><strong>The mappings are read across businesses, and that read is this issue's own reviewed
/// decision.</strong> A collector request carries a <em>denied</em> <c>BusinessScope</c>, so the
/// central query filters match no mapping at all and a match decided for a waiting key could reach
/// none of the rows that carry it. The statement therefore ignores the filters - once, in
/// <see cref="LoadAsync"/>, over the mappings whose <c>WaitingKey</c> is one of the keys the proposals
/// name. It is the <strong>third</strong> such path in a request, beside the Super Admin's one column
/// (issue #544) and the collector work list's business-free projection (issue #924), and what makes it
/// safe is what crosses the boundary: a waiting key, the search name and unit label it folds from, and
/// the mapping's own match state. Nothing about a business leaves this adapter - not an id, not a name,
/// not a product, and not a count of how many mappings a match moved, which would be a count of the
/// businesses tracking the product. See AGENTS.md § Tenant ownership and data isolation and
/// docs/architecture.md § Collector product matching.</para>
///
/// <para><strong>One <c>BEGIN IMMEDIATE</c> transaction, and the write lock is taken before the
/// reads.</strong> The idempotency key of a proposal - this run and this waiting key - has no unique
/// index to fall back on, because the record of what a run decided about a key <em>is</em> the
/// suggestion row it wrote. So "has this run already decided this key?" is only authoritative while
/// nothing can commit between the question and the write, which is exactly what the immediate write
/// lock gives (the same lock the switch of issue #938 and the machine delete of #896 take). The
/// authoritative read, the comparison and the mutation are one operation.</para>
///
/// <para><strong>Every rule is the Domain's, and this class is the sequence.</strong>
/// <see cref="AutomaticProductMatchPolicy"/> decides whether a match is obvious,
/// <see cref="ProductMatchSuggestionPolicy"/> decides what a suggestion may hold and what label a run's
/// suggestions carry, <see cref="ProductPriceTrackingPolicy"/> decides what a matched mapping must look
/// like, and <see cref="SupplierListingPolicy"/> decides what a moved listing may hang from. What is
/// here is the reading, the writing and the order they happen in.</para>
///
/// <para><strong>The order of the per-proposal checks is the design.</strong> Is the key one this run
/// issued; is the proposal itself storable; has this run already decided this key; has this batch
/// already claimed it; is anything still awaiting it; and only then what the Domain decides. A
/// malformed proposal is refused for its own reason <em>before</em> it claims the key, so a collector
/// that sends one bad proposal and one good one for a key gets the good one applied.</para>
/// </summary>
public sealed class EfPriceCollectorMatchStore : IPriceCollectorMatchStore
{
    private readonly AppDbContext _db;

    public EfPriceCollectorMatchStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ProductMatchReceipt?> SubmitAsync(
        ProductMatchBatch batch,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var run = await PriceCollectorOpenRun
            .FindAsync(_db, batch.RunId, asOfUtc, transaction.CommitAsync, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var context = await LoadAsync(run, batch, cancellationToken).ConfigureAwait(false);

        List<DecidedProposal> decided = [];

        foreach (var proposal in batch.Proposals)
        {
            decided.Add(Decide(proposal, context, asOfUtc));
        }

        // One save for every suggestion, moved listing and matched mapping the batch produced, inside
        // the one window that accepts the mappings' match columns on this denied scope. The lease is
        // closed before the commit: it grants a write, never a transaction.
        using (_db.BeginPriceCatalogueMatching())
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Read after the save, because a suggestion this batch created only has its key then - and a
        // receipt reporting a zero would be a key nothing could be looked up by.
        var results = decided.ConvertAll(proposal => new ProductMatchProposalResult(
            proposal.WaitingKey,
            proposal.Outcome,
            proposal.Reason,
            proposal.MasterProductId,
            proposal.SuggestionId));

        return new ProductMatchReceipt(
            run.Id,
            [.. results.Where(result => result.Outcome == ProductMatchProposalOutcome.AutoMatched)],
            [.. results.Where(result => result.Outcome == ProductMatchProposalOutcome.Tentative)],
            [.. results.Where(result => result.Outcome == ProductMatchProposalOutcome.Rejected)]);
    }

    /// <summary>
    /// Everything one batch is decided against, read once: the waiting keys this run issued work for,
    /// what this run has already decided, every suggestion already held against those keys, the
    /// mappings that carry them, the listings held under them, and the whole master catalogue.
    /// </summary>
    private async Task<MatchContext> LoadAsync(
        PriceCollectorRun run,
        ProductMatchBatch batch,
        CancellationToken cancellationToken)
    {
        var citedKeys = batch.Proposals
            .Select(proposal => (proposal.WaitingKey ?? string.Empty).Trim())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The keys this run was actually issued work for. A proposal about anything else is not an
        // answer to this run, whatever else is true of it.
        var issuedKeys = (await _db.PriceCollectorWorkItems
            .AsNoTracking()
            .Where(item => item.PriceCollectorRunId == run.Id && item.WaitingKey != null)
            .Select(item => item.WaitingKey!)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // Tracked, deliberately: a proposal whose pair already has a suggestion still waiting for a
        // reviewer reuses that row rather than writing a second one, and records this run's decision
        // on it by stamping the run label - which is the only record there is of what a run decided
        // about a key. Ordered by the row key, so the oldest suggestion of a pair is the one reused.
        var suggestions = await _db.ProductMatchSuggestions
            .Where(suggestion => citedKeys.Contains(suggestion.WaitingKey))
            .OrderBy(suggestion => suggestion.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The master catalogue in full, and deliberately not a bounded prefix of it: the condition is
        // "exactly one master product passes the checks", so a bound would make the answer depend on
        // where it fell. The work list's own MaxMasterProductsForMatching bounds a response, not a
        // check.
        var candidates = await _db.MasterProducts
            .AsNoTracking()
            .OrderBy(master => master.Id)
            .Select(master => new ProductMatchCandidate(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The reviewed cross-business read: one IgnoreQueryFilters statement, over the mappings that
        // carry a cited key, tracked because the match is about to write four of their columns. The
        // business key on those rows is used by nothing here and leaves nothing.
        var mappings = await _db.ProductPriceTrackings
            .IgnoreQueryFilters()
            .Where(tracking => citedKeys.Contains(tracking.WaitingKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listings = await _db.SupplierListings
            .Where(listing => listing.WaitingKey != null && citedKeys.Contains(listing.WaitingKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var runLabel = ProductMatchSuggestionPolicy.CollectorRunFor(run.Id);

        return new MatchContext(
            runLabel,
            issuedKeys,
            suggestions
                .Where(suggestion => string.Equals(
                    suggestion.CollectorRun,
                    runLabel,
                    StringComparison.Ordinal))
                .GroupBy(suggestion => suggestion.WaitingKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
            suggestions,
            candidates,
            mappings
                .GroupBy(mapping => mapping.WaitingKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ProductPriceTracking>)[.. group],
                    StringComparer.Ordinal),
            listings
                .GroupBy(listing => listing.WaitingKey!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SupplierListing>)[.. group],
                    StringComparer.Ordinal));
    }

    /// <summary>
    /// Decides one proposal and writes what the decision implies, in the fixed order the class summary
    /// states. It adds and changes entities and never saves: the caller owns the transaction, the
    /// matching window and the one save.
    /// </summary>
    private DecidedProposal Decide(
        ProductMatchProposal proposal,
        MatchContext context,
        DateTime asOfUtc)
    {
        var waitingKey = (proposal.WaitingKey ?? string.Empty).Trim();

        if (waitingKey.Length == 0 || !context.IssuedKeys.Contains(waitingKey))
        {
            return Rejected(waitingKey, AutomaticProductMatchPolicy.UnknownWaitingKeyReason);
        }

        ValidatedProposal validated;

        try
        {
            validated = ValidatedProposal.From(proposal, waitingKey);
        }
        catch (DomainValidationException refusal)
        {
            // Refused before the key is claimed: a proposal this catalogue could not store has
            // claimed nothing, so a storable proposal for the same key in the same batch still lands.
            return Rejected(waitingKey, refusal.Message);
        }

        if (context.ClaimedInThisBatch.Contains(waitingKey))
        {
            // A second proposal for a key this batch has already decided has no first outcome to be
            // answered with, so it is refused rather than letting ordering decide which was stored.
            // A repeat across requests does have one, which is the branch below.
            return Rejected(waitingKey, AutomaticProductMatchPolicy.DuplicateProposalInBatchReason);
        }

        if (context.DecidedThisRun.TryGetValue(waitingKey, out var already))
        {
            // The recorded decision, verbatim, and nothing written. Re-deciding would answer against
            // a catalogue that may have moved since the first answer was given.
            return new DecidedProposal(
                waitingKey,
                already.Status == ProductMatchSuggestionStatus.AutoApplied
                    ? ProductMatchProposalOutcome.AutoMatched
                    : ProductMatchProposalOutcome.Tentative,
                AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason,
                already.MasterProductId,
                Suggestion: null,
                already.Id);
        }

        context.ClaimedInThisBatch.Add(waitingKey);

        var awaiting = context.AwaitingMappings(waitingKey);

        if (awaiting.Count == 0)
        {
            return Rejected(waitingKey, AutomaticProductMatchPolicy.NothingAwaitingThisKeyReason);
        }

        // The spelling the work list published for this key: the ordinally first of the mappings'
        // own, which is the rule IPriceCatalogueWorkListStore states - so the description the backend
        // checks is the description the matcher was given.
        var representative = awaiting
            .OrderBy(mapping => mapping.SearchName, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.UnitLabel, StringComparer.Ordinal)
            .First();

        var decision = AutomaticProductMatchPolicy.Decide(
            representative.SearchName,
            representative.UnitLabel,
            validated.MasterProductId,
            validated.Confidence,
            context.Candidates,
            context.RefusedMasterProductIds(waitingKey));

        return decision.MasterProductId is { } masterProductId
            ? Apply(context, validated, awaiting, masterProductId, asOfUtc)
            : Keep(context, validated, decision.Refusal!, asOfUtc);
    }

    /// <summary>
    /// Applies one automatic match: the suggestion that records it, the mappings that were still
    /// awaiting it, and the listings the key had already collected.
    /// </summary>
    private DecidedProposal Apply(
        MatchContext context,
        ValidatedProposal proposal,
        IReadOnlyList<ProductPriceTracking> awaiting,
        long masterProductId,
        DateTime asOfUtc)
    {
        var suggestion = AddSuggestion(
            context,
            proposal,
            ProductMatchSuggestionStatus.AutoApplied,
            asOfUtc);

        foreach (var mapping in awaiting)
        {
            mapping.Status = ProductPriceTrackingStatus.AutoMatched;
            mapping.MasterProductId = masterProductId;
            mapping.MatchSource = ProductPriceTrackingMatchSource.CollectorAutomaticMatch;
            mapping.UpdatedAtUtc = asOfUtc;

            ProductPriceTrackingPolicy.RequireConsistentMatch(
                mapping.Status,
                mapping.MasterProductId,
                mapping.MatchSource);
        }

        MoveListings(context, proposal.WaitingKey, masterProductId, asOfUtc);

        return new DecidedProposal(
            proposal.WaitingKey,
            ProductMatchProposalOutcome.AutoMatched,
            Reason: null,
            masterProductId,
            suggestion,
            RecordedSuggestionId: null);
    }

    /// <summary>
    /// Moves every listing held under a matched waiting key onto the master product, keeping the key
    /// it was discovered under so an undo can move it back (#928) and leaving its match status exactly
    /// as it was.
    ///
    /// <para>A match moves where a listing <em>hangs</em>; it never decides that somebody confirmed
    /// the listing is that product, which is why a proposal stays a proposal and a flagged listing
    /// stays flagged. The active key has to be cleared because a listing belongs to exactly one of the
    /// two owners - the database checks that - and
    /// <see cref="SupplierListing.OriginWaitingKey"/> exists precisely so the provenance survives
    /// it.</para>
    /// </summary>
    private static void MoveListings(
        MatchContext context,
        string waitingKey,
        long masterProductId,
        DateTime asOfUtc)
    {
        foreach (var listing in context.ListingsUnder(waitingKey))
        {
            listing.MasterProductId = masterProductId;
            listing.OriginWaitingKey ??= waitingKey;
            listing.WaitingKey = null;
            listing.UpdatedAtUtc = asOfUtc;

            SupplierListingPolicy.RequireExactlyOneOwner(listing.MasterProductId, listing.WaitingKey);
        }
    }

    /// <summary>
    /// Keeps one proposal the backend could not call obvious as a tentative match for the Super Admin
    /// (#928), or reports why it was not kept at all.
    ///
    /// <para>Two refusals store nothing (<see cref="AutomaticProductMatchPolicy.StoresTentativeMatch"/>).
    /// A proposal whose pair - or whose suggested description - a reviewer is <em>already</em> looking
    /// at writes no second row either: that row is reused, so a reviewer is not handed the same
    /// decision once per run.</para>
    ///
    /// <para><strong>A reused row still records this run's decision.</strong> It is stamped with this
    /// run's label, because the suggestion row is the only record there is of what a run decided about
    /// a key: without the stamp the next request of the same run would find no decision for the key
    /// and would decide it again - against a proposal whose confidence, or whose proposed master
    /// product, may have changed in between. Only the label and the instant are stamped; the evidence
    /// the reviewer is to act on stays as the row first recorded it. So every decided proposal, kept
    /// or applied, leaves exactly one suggestion carrying this run's label for its key.</para>
    /// </summary>
    private DecidedProposal Keep(
        MatchContext context,
        ValidatedProposal proposal,
        string refusal,
        DateTime asOfUtc)
    {
        if (!AutomaticProductMatchPolicy.StoresTentativeMatch(refusal))
        {
            return Rejected(proposal.WaitingKey, refusal);
        }

        if (context.ReusableSuggestionFor(proposal) is { } existing)
        {
            existing.CollectorRun = context.RunLabel;
            existing.UpdatedAtUtc = asOfUtc;
            context.RecordDecision(existing);

            return new DecidedProposal(
                proposal.WaitingKey,
                ProductMatchProposalOutcome.Tentative,
                AutomaticProductMatchPolicy.AlreadySuggestedReason,
                proposal.MasterProductId,
                Suggestion: null,
                existing.Id);
        }

        var suggestion = AddSuggestion(
            context,
            proposal,
            ProductMatchSuggestionStatus.Tentative,
            asOfUtc);

        return new DecidedProposal(
            proposal.WaitingKey,
            ProductMatchProposalOutcome.Tentative,
            refusal,
            proposal.MasterProductId,
            suggestion,
            RecordedSuggestionId: null);
    }

    /// <summary>
    /// Records one suggestion - the matcher's own reason, its confidence as the proportion the three
    /// levels are stored as, and this run's label, which is also the proposal's idempotency key.
    /// </summary>
    private ProductMatchSuggestion AddSuggestion(
        MatchContext context,
        ValidatedProposal proposal,
        ProductMatchSuggestionStatus status,
        DateTime asOfUtc)
    {
        var suggestion = new ProductMatchSuggestion
        {
            WaitingKey = proposal.WaitingKey,
            MasterProductId = proposal.MasterProductId,
            SuggestedBrand = proposal.SuggestedBrand,
            SuggestedName = proposal.SuggestedName,
            SuggestedSizeValue = proposal.SuggestedSizeValue,
            SuggestedSizeUnit = proposal.SuggestedSizeUnit,
            Confidence = proposal.StoredConfidence,
            Reason = proposal.Reason,
            CollectorRun = context.RunLabel,
            Status = status,
            CreatedAtUtc = asOfUtc,
            UpdatedAtUtc = asOfUtc,
        };

        _db.ProductMatchSuggestions.Add(suggestion);
        context.Record(suggestion);

        return suggestion;
    }

    private static DecidedProposal Rejected(string waitingKey, string reason) =>
        new(
            waitingKey,
            ProductMatchProposalOutcome.Rejected,
            reason,
            MasterProductId: null,
            Suggestion: null,
            RecordedSuggestionId: null);

    /// <summary>
    /// One decided proposal before the save that gives its suggestion a key. It holds the entity
    /// rather than the id for the reason a decided result row does (issue #937): a row created in this
    /// batch has no id until the batch is saved, and an answer reporting a zero would be a key nothing
    /// could be looked up by.
    /// </summary>
    private sealed record DecidedProposal(
        string WaitingKey,
        ProductMatchProposalOutcome Outcome,
        string? Reason,
        long? MasterProductId,
        ProductMatchSuggestion? Suggestion,
        long? RecordedSuggestionId)
    {
        public long? SuggestionId => Suggestion?.Id ?? RecordedSuggestionId;
    }

    /// <summary>
    /// One submitted proposal once the Domain has said what of it may be stored: the key it is about,
    /// the one proposal it carries, and the confidence as the proportion it is stored as.
    /// </summary>
    private sealed record ValidatedProposal(
        string WaitingKey,
        long? MasterProductId,
        string? SuggestedBrand,
        string? SuggestedName,
        decimal? SuggestedSizeValue,
        MasterProductSizeUnit? SuggestedSizeUnit,
        ProductMatchConfidence Confidence,
        decimal StoredConfidence,
        string Reason)
    {
        /// <exception cref="DomainValidationException">
        /// Something in the proposal is not a value this catalogue stores. Nothing is written.
        /// </exception>
        public static ValidatedProposal From(ProductMatchProposal proposal, string waitingKey)
        {
            ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
                proposal.MasterProductId,
                proposal.SuggestedBrand,
                proposal.SuggestedName,
                proposal.SuggestedSizeValue,
                proposal.SuggestedSizeUnit);

            if (!AutomaticProductMatchPolicy.IsDeclaredConfidence(proposal.Confidence))
            {
                throw new DomainValidationException(
                    AutomaticProductMatchPolicy.UnknownConfidenceMessage);
            }

            var describesNewMaster = proposal.MasterProductId is null;

            return new ValidatedProposal(
                waitingKey,
                proposal.MasterProductId,
                describesNewMaster
                    ? MasterProductPolicy.NormaliseBrand(proposal.SuggestedBrand)
                    : null,
                describesNewMaster
                    ? MasterProductPolicy.NormaliseName(proposal.SuggestedName)
                    : null,
                describesNewMaster
                    ? MasterProductPolicy.NormaliseSize(
                        proposal.SuggestedSizeValue!.Value,
                        proposal.SuggestedSizeUnit!.Value)
                    : null,
                describesNewMaster ? proposal.SuggestedSizeUnit : null,
                proposal.Confidence,
                ProductMatchSuggestionPolicy.NormaliseConfidence(
                    AutomaticProductMatchPolicy.ConfidenceProportion(proposal.Confidence)),
                ProductMatchSuggestionPolicy.NormaliseReason(proposal.Reason));
        }

        /// <summary>
        /// The description a suggested new master product names, folded by the master catalogue's own
        /// key rule - which is how "this key already has this suggestion" is asked of a description
        /// rather than of a row key.
        /// </summary>
        public string? SuggestedDescriptionKey => MasterProductId is null
            ? MasterProductPolicy.NormalisedKey(
                SuggestedBrand,
                SuggestedName,
                SuggestedSizeValue!.Value,
                SuggestedSizeUnit!.Value)
            : null;
    }

    /// <summary>Everything one batch is decided against, read once before any of it is decided.</summary>
    private sealed class MatchContext
    {
        private readonly Dictionary<string, ProductMatchSuggestion> _decidedThisRun;
        private readonly List<ProductMatchSuggestion> _suggestions;
        private readonly Dictionary<string, IReadOnlyList<ProductPriceTracking>> _mappings;
        private readonly Dictionary<string, IReadOnlyList<SupplierListing>> _listings;

        public MatchContext(
            string runLabel,
            HashSet<string> issuedKeys,
            Dictionary<string, ProductMatchSuggestion> decidedThisRun,
            List<ProductMatchSuggestion> suggestions,
            IReadOnlyCollection<ProductMatchCandidate> candidates,
            Dictionary<string, IReadOnlyList<ProductPriceTracking>> mappings,
            Dictionary<string, IReadOnlyList<SupplierListing>> listings)
        {
            RunLabel = runLabel;
            IssuedKeys = issuedKeys;
            Candidates = candidates;
            _decidedThisRun = decidedThisRun;
            _suggestions = suggestions;
            _mappings = mappings;
            _listings = listings;
        }

        /// <summary>The label this run's suggestions are stored under, which is their idempotency key.</summary>
        public string RunLabel { get; }

        /// <summary>The waiting keys this run was issued work for.</summary>
        public HashSet<string> IssuedKeys { get; }

        /// <summary>Every master product this catalogue holds, as the checks compare them.</summary>
        public IReadOnlyCollection<ProductMatchCandidate> Candidates { get; }

        /// <summary>What this run has already decided, by waiting key.</summary>
        public IReadOnlyDictionary<string, ProductMatchSuggestion> DecidedThisRun => _decidedThisRun;

        /// <summary>
        /// The keys this batch has already claimed. A repeat across requests is answered with the
        /// recorded decision; a repeat inside one batch has no decision to be answered with, so it is
        /// refused rather than letting ordering decide which was stored.
        /// </summary>
        public HashSet<string> ClaimedInThisBatch { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The mappings under one key that are still <em>awaiting a match</em>, which is the only
        /// state an automatic match may change. A mapping a person settled, one an earlier run
        /// matched, one that was looked for and not found and one suspended on its own product's
        /// details are all left exactly as they are - and a mapping whose product has been renamed or
        /// relabelled since the work list was issued carries another key now, so it is not here at
        /// all.
        /// </summary>
        public IReadOnlyList<ProductPriceTracking> AwaitingMappings(string waitingKey) =>
            _mappings.TryGetValue(waitingKey, out var mappings)
                ? [.. mappings.Where(mapping =>
                    mapping.Status == ProductPriceTrackingStatus.Requested)]
                : [];

        /// <summary>The listings still held under one key, which a match moves to the master product.</summary>
        public IReadOnlyList<SupplierListing> ListingsUnder(string waitingKey) =>
            _listings.TryGetValue(waitingKey, out var listings) ? listings : [];

        /// <summary>
        /// The master products rejected or undone for one key, which may never be matched to it again
        /// (<see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>).
        /// </summary>
        public IReadOnlySet<long> RefusedMasterProductIds(string waitingKey) =>
            _suggestions
                .Where(suggestion =>
                    string.Equals(suggestion.WaitingKey, waitingKey, StringComparison.Ordinal)
                    && suggestion.MasterProductId is not null
                    && ProductMatchSuggestionPolicy.BlocksAutomaticMatching(suggestion.Status))
                .Select(suggestion => suggestion.MasterProductId!.Value)
                .ToHashSet();

        /// <summary>
        /// The suggestion this proposal is to be kept as rather than written again, or
        /// <see langword="null"/> when there is none: a suggestion of the same
        /// <c>(key, master product)</c> pair, or of the same suggested description under the same key,
        /// that is <em>still waiting for a reviewer's decision</em>.
        ///
        /// <para>A pair whose suggestions have all been decided is not reused, and that is the same
        /// rule rather than an exception to it: the row reused here is stamped with the run that
        /// decided the key, and a row saying an <em>earlier</em> run applied the match - or that a
        /// person accepted it - must not be restamped as this run's tentative decision. A decided row
        /// is also not the duplicate the reuse exists to prevent: what a reviewer must not be handed
        /// twice is one proposal still waiting for them.</para>
        /// </summary>
        public ProductMatchSuggestion? ReusableSuggestionFor(ValidatedProposal proposal)
        {
            var descriptionKey = proposal.SuggestedDescriptionKey;

            return _suggestions
                .Where(suggestion =>
                    string.Equals(suggestion.WaitingKey, proposal.WaitingKey, StringComparison.Ordinal)
                    && suggestion.Status == ProductMatchSuggestionStatus.Tentative)
                .FirstOrDefault(suggestion => proposal.MasterProductId is { } proposed
                    ? suggestion.MasterProductId == proposed
                    : suggestion.MasterProductId is null && DescribesSame(suggestion, descriptionKey));
        }

        /// <summary>Remembers a suggestion this batch wrote, so a later proposal of it sees it too.</summary>
        public void Record(ProductMatchSuggestion suggestion)
        {
            _suggestions.Add(suggestion);
            RecordDecision(suggestion);
        }

        /// <summary>
        /// Remembers the suggestion that records what this run decided about one key, so a later
        /// request of the same run is answered with that decision instead of deciding the key again.
        /// The row itself carries this run's label, which is how the same answer survives the request
        /// that recorded it.
        /// </summary>
        public void RecordDecision(ProductMatchSuggestion suggestion)
        {
            _decidedThisRun[suggestion.WaitingKey] = suggestion;
        }

        /// <summary>
        /// Whether a stored suggestion describes the same new master product, compared by the master
        /// catalogue's own key rule.
        ///
        /// <para>Each part is checked for being there before the key is built, because the key rule
        /// refuses a description it would not store - and a stored row this comparison cannot fold is
        /// simply not the same description, never an exception on a path that was only asking.</para>
        /// </summary>
        private static bool DescribesSame(ProductMatchSuggestion suggestion, string? descriptionKey) =>
            descriptionKey is not null
            && !string.IsNullOrWhiteSpace(suggestion.SuggestedBrand)
            && !string.IsNullOrWhiteSpace(suggestion.SuggestedName)
            && suggestion.SuggestedSizeValue is { } size
            && suggestion.SuggestedSizeUnit is { } unit
            && MasterProductPolicy.IsDeclaredSizeUnit(unit)
            && size > 0m
            && string.Equals(
                MasterProductPolicy.NormalisedKey(
                    suggestion.SuggestedBrand,
                    suggestion.SuggestedName,
                    size,
                    unit),
                descriptionKey,
                StringComparison.Ordinal);
    }
}
