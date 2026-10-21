using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Which rule filled a sale's <see cref="NayaxSales.ProductId"/> (issue #758). Numbered from 1, so
/// an unwritten integer column is not a rule.
/// </summary>
public enum NayaxSaleProductResolutionRule
{
    /// <summary>
    /// The sale's stored id matched no product of its business - never a conflicting sale - and its
    /// stored <c>ProductName</c> matched exactly one product under the current
    /// <c>ProductMatcher</c> name rule. Applied once, by the release's own migration.
    /// </summary>
    UniqueProductName = 1,

    /// <summary>
    /// A human named the product, because the sale's two candidates disagreed and nothing may pick
    /// between them automatically. Applied by the <c>resolve-nayax-sale-products</c> command.
    /// </summary>
    OperatorDecision = 2,

    /// <summary>
    /// A human linked the sale's provider product id to a local product in the catalogue review and
    /// then explicitly asked for the sales carrying it to be resolved (issue #738). The provider id
    /// is the match - never the name - and the sale's own stored id had to carry known raw
    /// provenance (<c>NayaxSales.ProviderProductIdIsRaw</c>), so nothing provenance-unknown is read
    /// as an identifier. Applied by <c>POST api/catalogue-review/unresolved-sales/apply</c>.
    /// </summary>
    ProviderCatalogueLink = 3,
}

/// <summary>
/// The audit trail of every sale whose local product was filled in after the fact (issue #758,
/// parent #329 decision A2c): the one-off name resolution the release's migration performs, and
/// each audited human decision about a <see cref="NayaxSaleProductConflict"/>.
///
/// <para>One row per sale per resolution, carrying the old value, the new value and the rule used -
/// plus, for a decision, the candidates that were ambiguous, who decided and when. Append-only: a
/// resolution is evidence about a financial attribution and is never updated or deleted.</para>
///
/// <para><strong>The operation is idempotent because it only ever fills a null.</strong> A sale that
/// already has a product is left exactly as it is and writes no row, so re-running the migration
/// step or the command changes nothing and cannot move a sale off the product it is already
/// attributed to.</para>
/// </summary>
public class NayaxSaleProductResolution : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>The local key of the <see cref="NayaxSales"/> row that was resolved.</summary>
    public long NayaxSaleId { get; set; }

    /// <summary>The remote transaction identifier of that sale.</summary>
    public long TransactionId { get; set; }

    /// <summary>
    /// The sale's product before this resolution. Always <c>null</c> today, because only an
    /// unresolved sale is ever resolved; it is recorded explicitly so the audit row states the
    /// change rather than implying it.
    /// </summary>
    public long? PreviousProductId { get; set; }

    /// <summary>The product the sale was attributed to.</summary>
    public long NewProductId { get; set; }

    /// <summary>Which rule produced <see cref="NewProductId"/>.</summary>
    public NayaxSaleProductResolutionRule Rule { get; set; }

    /// <summary>
    /// For an <see cref="NayaxSaleProductResolutionRule.OperatorDecision"/>, the candidate product
    /// ids the decision chose between, as the invariant text <c>"&lt;direct&gt;,&lt;external&gt;"</c>;
    /// <c>null</c> for a name resolution, which had one candidate by definition.
    /// </summary>
    public string? CandidateProductIds { get; set; }

    /// <summary>
    /// Who decided, for an operator decision: the identity the operator supplied on the command
    /// line. <c>null</c> for the migration's name step, whose actor is the release itself.
    /// </summary>
    public string? DecidedBy { get; set; }

    /// <summary>When the resolution was applied.</summary>
    public DateTime ResolvedAtUtc { get; set; }
}
