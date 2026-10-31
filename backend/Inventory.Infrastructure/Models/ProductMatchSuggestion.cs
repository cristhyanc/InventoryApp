using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One suggested match for one waiting key (issue #923, parent #817), for the automatic matching of
/// #941 and the review of #928: what the matcher thinks a waiting product is, how sure it is, why,
/// which run said so, and what was then done about it.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> A
/// suggestion is about a waiting <em>key</em> - a folded product name and unit label - and never
/// about the business whose product produced that key. That is what keeps one business from being
/// able to learn that another is tracking anything, and it is why there is no <c>BusinessId</c> here
/// to carry.</para>
///
/// <para><strong>Nothing is ever deleted, and that is the point of the table.</strong> A suggestion
/// a person <see cref="ProductMatchSuggestionStatus.Rejected"/>, or
/// <see cref="ProductMatchSuggestionStatus.Undone"/> after it had been applied, is kept so the
/// matcher never applies that (waiting key, master product) pair again. A matcher that forgot a
/// rejection would re-apply it on its next run, and the person who rejected it would have to reject
/// it again every run - so the evidence has to outlive the refusal.</para>
///
/// <para><strong>One proposal per row.</strong> Either <see cref="MasterProductId"/> names an
/// existing master product, or the four <c>Suggested*</c> columns describe a new one to create -
/// never both and never neither. Those are two different things to do about one waiting key, and a
/// row holding both would leave a reviewer with no way to know which was meant; the database checks
/// it, beside <c>ProductMatchSuggestionPolicy.HasExactlyOneProposal</c>.</para>
/// </summary>
public class ProductMatchSuggestion
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The waiting key this suggestion is about: <c>PriceCatalogueWaitingKey.For</c> of a tracked
    /// product's name and unit label. Plain text, carrying no business or product identity of any
    /// kind.
    /// </summary>
    public string WaitingKey { get; set; } = string.Empty;

    /// <summary>
    /// The existing master product being proposed, or <see langword="null"/> when a new one is being
    /// described instead. Restricted on delete, so a master product a suggestion refers to - a
    /// rejected one above all - cannot be removed from under the record that refers to it.
    /// </summary>
    public long? MasterProductId { get; set; }

    /// <summary>The proposed master product itself, while one is named.</summary>
    public virtual MasterProduct? MasterProduct { get; set; }

    /// <summary>The brand of the new master product being suggested, or <see langword="null"/>.</summary>
    public string? SuggestedBrand { get; set; }

    /// <summary>The name or variant of the new master product being suggested, or <see langword="null"/>.</summary>
    public string? SuggestedName { get; set; }

    /// <summary>
    /// The individual size of the new master product being suggested, or <see langword="null"/>. A
    /// positive decimal when present - the database checks that - on
    /// <see cref="MasterProduct.SizeValue"/>'s terms.
    /// </summary>
    public decimal? SuggestedSizeValue { get; set; }

    /// <summary>
    /// What <see cref="SuggestedSizeValue"/> is measured in, from the same closed unit list a master
    /// product's own size uses, or <see langword="null"/>.
    /// </summary>
    public MasterProductSizeUnit? SuggestedSizeUnit { get; set; }

    /// <summary>
    /// How sure the matcher is, as a proportion between 0 and 1. It decides nothing here: whether a
    /// confidence is high enough to apply automatically is #941's threshold, deliberately not this
    /// table's.
    /// </summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// Why the matcher proposed this - the evidence a reviewer reads. Required, because a suggestion
    /// nobody can account for is one a reviewer can only guess at.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The collector run that produced the row. Opaque text that nothing parses, so the collector of
    /// #924 may name its runs however it names them.
    ///
    /// <para><strong>It is written once and never rewritten.</strong> For the automatic matching of
    /// #941 it is also the idempotency key of a proposal, together with
    /// <see cref="WaitingKey"/> - so relabelling an existing row would erase the record of what the
    /// run that wrote it decided. A run that repeats a proposal somebody is already looking at
    /// records its own <see cref="ProductMatchSuggestionStatus.Repeated"/> row instead.</para>
    /// </summary>
    public string CollectorRun { get; set; } = string.Empty;

    /// <summary>
    /// What has happened to the suggestion: tentative, auto-applied, accepted, rejected, undone, or
    /// one run's record of having repeated a proposal a reviewer is already looking at. Rejected and
    /// undone are the ones that are kept forever.
    /// </summary>
    public ProductMatchSuggestionStatus Status { get; set; } = ProductMatchSuggestionStatus.Tentative;

    /// <summary>When the suggestion was recorded, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When its status last changed; equal to <see cref="CreatedAtUtc"/> while it is still as the
    /// collector left it.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
