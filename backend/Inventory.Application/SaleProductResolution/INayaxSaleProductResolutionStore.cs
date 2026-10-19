using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.SaleProductResolution;

/// <summary>
/// One stored sale that carries no local product (issue #758), as the audited resolution operation
/// reads it.
///
/// <para><c>ConflictDirectCandidateProductId</c> and <c>ConflictExternalCandidateProductId</c> are
/// set only for a sale the release's migration marked <em>conflicting</em>: its stored number named
/// one product directly and a different one through that product's recorded provider id, so there
/// was no honest way to choose and nothing may choose automatically. They are the two candidates
/// recorded as the reason, shown to the operator and written into the audit row.</para>
///
/// <para><c>IsCosted</c> says the sale carries a historical cost today, and
/// <c>IsAttributableByName</c> that exactly one product's name matches the name this sale reported.
/// Those are the two kinds of sale the release's failure gate refuses to activate over: leaving a
/// costed one unresolved would drop it from every future cost replay while it still carries COGS,
/// and leaving an attributable one unresolved would move the product-level revenue and machine-stock
/// attribution the pre-release readers gave it through <c>ProductMatcher</c>'s name fallback.</para>
///
/// <para><c>IsAttributableByName</c> is decided by <see cref="ResolveNayaxSaleProducts"/> over the
/// real Domain rule, never by the store, which is why it is also the honest answer for the sales the
/// migration's SQL fold could not decide about.</para>
/// </summary>
public sealed record UnresolvedNayaxSale(
    long SaleId,
    long TransactionId,
    long MachineId,
    DateTime AuthorizationTimeUtc,
    string? ProductName,
    long? ProviderProductId,
    bool IsCosted,
    long? ConflictDirectCandidateProductId,
    long? ConflictExternalCandidateProductId,
    bool IsAttributableByName = false);

/// <summary>One sale's audited resolution, as it was applied.</summary>
public sealed record AppliedNayaxSaleProductResolution(long SaleId, long TransactionId, long ProductId);

/// <summary>
/// Narrow persistence port for the audited sale-to-product resolution (issue #758), owned by the
/// Application layer and implemented by
/// <c>Inventory.Infrastructure.Persistence.EfNayaxSaleProductResolutionStore</c>.
///
/// It decides nothing. Reads and writes are scoped to the caller's business by the central query
/// filter and ownership enforcer alone, never by a predicate of its own, and the only write it
/// offers fills a sale's <c>ProductId</c> while it is still null and appends the audit row that
/// records it.
/// </summary>
public interface INayaxSaleProductResolutionStore
{
    /// <summary>Every unresolved sale of the caller's business, oldest first.</summary>
    Task<IReadOnlyList<UnresolvedNayaxSale>> ListUnresolvedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The caller's product catalogue, as the Domain <see cref="ProductMatcher"/> consumes it. It is
    /// read to <em>report</em> which unresolved sales a product's name attributes today - the rule
    /// the release's gate refuses to activate over - and never to attribute a stored sale, which is
    /// the writer's decision alone.
    /// </summary>
    Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The names of the caller's products among <paramref name="productIds"/>. A product id the
    /// caller's business does not hold is simply absent, which is how a decision naming another
    /// business's product is refused before any write is staged.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetProductNamesAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="decisions"/> and answers the sale ids it actually settled, in the
    /// order they were given. Each sale's <c>ProductId</c> is filled <strong>only while it is still
    /// null</strong>, by one guarded statement rather than a value read earlier, and the audit row
    /// recording the previous value, the new value, the rule, the candidates, who decided and when
    /// is committed with it or not at all. A sale some other writer settled first is absent from
    /// the answer and nothing is written for it - which is what makes a repeated run, and two
    /// overlapping runs, a no-op that can never move a decided sale.
    /// </summary>
    Task<IReadOnlyList<long>> ApplyDecisionsAsync(
        IReadOnlyCollection<NayaxSaleProductDecision> decisions,
        string decidedBy,
        CancellationToken cancellationToken);
}
