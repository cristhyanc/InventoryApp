using System.Text.Json.Serialization;
using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One started provider catalogue review (issue #738, parent #329 decision C1): the snapshot of the
/// provider's catalogue an operator is deciding about, and - once it has one - the instant and
/// identity that applied it.
///
/// <para><strong>Why it is persisted at all.</strong> The apply is authorised by a review rather than
/// by a payload: the operator submits decisions about items this row's own
/// <see cref="ProviderCatalogueReviewItem"/> rows name, and every provider name, price and group id
/// the apply writes comes from those rows rather than from the request. That is what makes "apply the
/// same review twice" a question with an answer - <see cref="AppliedAtUtc"/> - instead of two
/// unrelated writes.</para>
///
/// <para><strong>One pending review per business.</strong> Starting a review replaces the business's
/// unapplied one, because a review is a reading of the provider's catalogue at an instant and an
/// older reading is not a second opinion. An applied review is never deleted or rewritten: it is the
/// record of what was decided.</para>
/// </summary>
public class ProviderCatalogueReview : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>
    /// Which provider's catalogue this review read. Recorded for the reason
    /// <c>NayaxSales.Provider</c> is: the external ids on the items are that provider's, and nothing
    /// else in the row says which system issued them.
    /// </summary>
    public ExternalProvider Provider { get; set; } = ExternalProvider.Nayax;

    /// <summary>When the provider's catalogue was read into this review.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When this review was applied, or <c>null</c> while it is still pending. A review is applied
    /// once: a second apply of an applied review writes nothing and reports that it changed nothing.
    /// </summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>
    /// Who applied it, as the authenticated actor's directory identity. <c>null</c> while pending.
    /// Never an email address or a display name.
    /// </summary>
    public string? AppliedBy { get; set; }

    [JsonIgnore]
    public virtual ICollection<ProviderCatalogueReviewItem> Items { get; set; } =
        new List<ProviderCatalogueReviewItem>();
}
