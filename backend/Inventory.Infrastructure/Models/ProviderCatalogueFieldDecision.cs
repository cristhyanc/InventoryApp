using System.Text.Json.Serialization;
using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// The last provider value a negotiable catalogue field was decided about, per product per field
/// (issue #738, parent #329 decision C1).
///
/// <para><strong>This is the row that makes a skip stick.</strong> Without it, an operator who kept
/// their own price over the provider's would be asked the same question on every review, which is
/// indistinguishable from the review not remembering anything. With it, the provider value is
/// proposed again only once the provider has <em>changed it again</em>
/// (<c>CatalogueReviewPolicy.IsProposed</c>).</para>
///
/// <para><strong>One row per product and field, replaced rather than appended.</strong> The question
/// it answers is "what did we last ask about?", which has one current answer; the history of the
/// asking is not financial data and is not kept. <see cref="ProviderValue"/> is the opaque text
/// <c>Inventory.Domain.Providers.ProviderCatalogueFieldValue</c> renders, so the comparison is exact
/// and culture-free.</para>
/// </summary>
public class ProviderCatalogueFieldDecision : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>
    /// The local product the decision is about, as a foreign key on <c>(BusinessId, ProductId)</c>
    /// against the products' business key. It cascades: a decision about a deleted product is not a
    /// reason the delete fails, and it has nothing left to say.
    /// </summary>
    public long ProductId { get; set; }

    /// <summary>Which negotiable field was decided.</summary>
    public NegotiableCatalogueField Field { get; set; }

    /// <summary>The provider value that was decided about, as comparable text.</summary>
    public string ProviderValue { get; set; } = string.Empty;

    /// <summary>Whether the operator kept the local value or took the provider's.</summary>
    public ProviderFieldDecision Decision { get; set; }

    /// <summary>When it was decided.</summary>
    public DateTime DecidedAtUtc { get; set; }
}
