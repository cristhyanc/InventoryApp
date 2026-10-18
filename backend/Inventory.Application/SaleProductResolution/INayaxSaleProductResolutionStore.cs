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
/// <para><c>IsCosted</c> says the sale carries a historical cost today. Those are the sales the
/// release's failure gate refuses to activate over, because leaving one unresolved would drop it
/// from every future cost replay while it still carries COGS.</para>
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
    long? ConflictExternalCandidateProductId);

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
    /// The names of the caller's products among <paramref name="productIds"/>. A product id the
    /// caller's business does not hold is simply absent, which is how a decision naming another
    /// business's product is refused before any write is staged.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetProductNamesAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// Stages the decision on one sale: sets its <c>ProductId</c> and appends the audit row with
    /// the previous value, the new value, the rule, the recorded candidates, who decided and when.
    /// Answers <see langword="false"/> and stages nothing when the caller's business has no
    /// unresolved sale with that key - which is what makes a repeated run a no-op.
    /// </summary>
    Task<bool> StageDecisionAsync(
        long saleId, long productId, string decidedBy, CancellationToken cancellationToken);

    /// <summary>Persists everything staged so far.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
