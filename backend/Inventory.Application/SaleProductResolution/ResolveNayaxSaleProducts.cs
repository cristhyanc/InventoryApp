using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.SaleProductResolution;

/// <summary>One operator decision: this sale belongs to this local product.</summary>
public sealed record NayaxSaleProductDecision(long SaleId, long ProductId);

/// <summary>
/// What one business's run of <see cref="ResolveNayaxSaleProducts"/> found and did. A dry run
/// reports the same shape with <see cref="Applied"/> empty and nothing written.
/// </summary>
public sealed record NayaxSaleProductResolutionReport(
    IReadOnlyList<UnresolvedNayaxSale> Unresolved,
    IReadOnlyList<AppliedNayaxSaleProductResolution> Applied,
    IReadOnlyList<string> Refusals,
    bool DryRun)
{
    /// <summary>
    /// Unresolved sales that carry a historical cost today. These are exactly the sales the
    /// release's failure gate refuses to start over, so this count is what an operator has to get
    /// to zero before the deployment can come up.
    /// </summary>
    public int UnresolvedCostedCount => Unresolved.Count(sale => sale.IsCosted);

    /// <summary>Unresolved sales whose two candidate products disagreed.</summary>
    public int ConflictingCount =>
        Unresolved.Count(sale => sale.ConflictDirectCandidateProductId.HasValue);

    /// <summary>
    /// Unresolved sales that exactly one product's name attributes today. These are the other sales
    /// the release's failure gate refuses to start over, costed or not: the pre-release readers gave
    /// each of them that product through <c>ProductMatcher</c>'s name fallback, so leaving one
    /// unresolved would move its product-level revenue and machine-stock attribution.
    /// </summary>
    public int NameAttributableCount => Unresolved.Count(sale => sale.IsAttributableByName);
}

/// <summary>
/// The audited, idempotent operation that gives an unresolved stored sale its local product
/// (issue #758, parent #329 decision A2c).
///
/// <para>It exists because two things must never be guessed. A sale the release's migration marked
/// <strong>conflicting</strong> - its stored number named one product directly and a different one
/// through a recorded provider id - is never resolved automatically, by name or otherwise; and a
/// sale the migration's name step could not settle (an ambiguous name, an absent one, or a
/// catalogue the SQL fold cannot be proved to match the Domain rule over) stays unresolved too. A
/// human names the product, with their identity and the instant recorded beside the candidates they
/// chose between.</para>
///
/// <para><strong>Idempotent, because it only ever fills a null.</strong> A sale that already has a
/// product is not in the unresolved list, so a decision about it is refused rather than applied: a
/// rerun writes nothing and cannot move a sale off the product it is already attributed to. Nothing
/// here updates or deletes an audit row.</para>
///
/// <para><strong>It never chooses anything itself.</strong> With no decisions it is a report: what
/// is unresolved, which of those are conflicting, which carry a cost today and which exactly one
/// product's name attributes today - the two kinds of sale the release's failure gate refuses to
/// start over. The name answer is the Domain <see cref="ProductMatcher"/> rule evaluated here, which
/// is why it also covers the catalogues the migration's SQL fold could not decide about. Every write
/// comes from a decision the operator typed.</para>
/// </summary>
public sealed class ResolveNayaxSaleProducts
{
    private readonly INayaxSaleProductResolutionStore _store;

    public ResolveNayaxSaleProducts(INayaxSaleProductResolutionStore store) => _store = store;

    public async Task<NayaxSaleProductResolutionReport> Handle(
        IReadOnlyCollection<NayaxSaleProductDecision> decisions,
        string decidedBy,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var unresolved = await ListUnresolvedAsync(cancellationToken);
        var unresolvedById = unresolved.ToDictionary(sale => sale.SaleId);
        var applicable = decisions.Where(decision => unresolvedById.ContainsKey(decision.SaleId)).ToList();

        // Only the products the caller's business actually holds. A decision naming anything else -
        // a deleted product, a typo, another business's row - is refused here, before anything is
        // staged, rather than left for the foreign key to turn into a save failure.
        var productNames = await _store.GetProductNamesAsync(
            applicable.Select(decision => decision.ProductId).Distinct().ToList(), cancellationToken);

        var refusals = new List<string>();
        var decidable = new List<NayaxSaleProductDecision>();

        foreach (var decision in applicable)
        {
            if (!productNames.ContainsKey(decision.ProductId))
            {
                refusals.Add(
                    $"Sale {decision.SaleId}: product {decision.ProductId} is not a product of this "
                        + "business, so the decision was refused and nothing was written.");
                continue;
            }

            decidable.Add(decision);
        }

        // One call, because the store settles each sale with a guarded statement that fills its
        // product only while it is still null and commits the audit row with it. A sale another
        // writer decided between the read above and this write is simply not in the answer: the
        // decision that arrived second never moves it and never leaves an audit row claiming the
        // previous value was null.
        var settled = dryRun || decidable.Count == 0
            ? decidable.Select(decision => decision.SaleId).ToHashSet()
            : (await _store.ApplyDecisionsAsync(decidable, decidedBy, cancellationToken)).ToHashSet();

        var applied = decidable
            .Where(decision => settled.Contains(decision.SaleId))
            .Select(decision => new AppliedNayaxSaleProductResolution(
                decision.SaleId, unresolvedById[decision.SaleId].TransactionId, decision.ProductId))
            .ToList();

        refusals.AddRange(decidable
            .Where(decision => !settled.Contains(decision.SaleId))
            .Select(decision =>
                $"Sale {decision.SaleId}: no unresolved sale with that key, so nothing was written."));

        // Re-read after an applied run so the report states what is still unresolved, not what was
        // unresolved before the decisions landed.
        var remaining = !dryRun && applied.Count > 0
            ? await ListUnresolvedAsync(cancellationToken)
            : unresolved;

        return new NayaxSaleProductResolutionReport(remaining, applied, refusals, dryRun);
    }

    /// <summary>
    /// The store's unresolved list with <see cref="UnresolvedNayaxSale.IsAttributableByName"/>
    /// decided here, over the caller's catalogue and the Domain rule.
    /// </summary>
    private async Task<IReadOnlyList<UnresolvedNayaxSale>> ListUnresolvedAsync(
        CancellationToken cancellationToken)
    {
        var unresolved = await _store.ListUnresolvedAsync(cancellationToken);
        if (unresolved.Count == 0)
            return unresolved;

        var catalogue = await _store.GetProductCandidatesAsync(cancellationToken);
        return unresolved
            .Select(sale => sale with { IsAttributableByName = IsAttributableByName(catalogue, sale) })
            .ToList();
    }

    /// <summary>
    /// Whether exactly one product's name attributes this sale under the Domain rule - the same
    /// <see cref="ProductMatcher"/> name fallback every reader applied before the association was
    /// persisted, so this answers "was this sale attributed to a product today?" for the half of
    /// issue #758's failure gate that is not about cost.
    ///
    /// <para>A conflicting sale is never attributable this way: its stored number names a product
    /// directly, so the name fallback never ran for it, and nothing but an audited decision may
    /// settle a disagreement two recorded identifiers already raised.</para>
    /// </summary>
    private static bool IsAttributableByName(
        IReadOnlyList<ProductMatchCandidate> catalogue, UnresolvedNayaxSale sale) =>
        !sale.ConflictDirectCandidateProductId.HasValue
        && ProductMatcher.Match(catalogue, null, sale.ProductName) is not null;
}
