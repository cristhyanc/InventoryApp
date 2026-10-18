using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One stored sale whose two candidate products disagreed when issue #758's migration resolved the
/// local product of every existing sale, recorded with both candidates as the reason.
///
/// <para>Step 1 of that migration fills <see cref="NayaxSales.ProductId"/> only when the stored
/// <c>NayaxProductId</c> names a product of the same business <em>and</em>, if any product of that
/// business carries that number as its <c>ExternalProductId</c> (issue #759), it is the same
/// product. When the direct id and the external id point at different products there is no honest
/// answer - the column's pre-migration provenance is mixed, so the stored number may be either kind
/// of identifier - and the sale stays unresolved with a row here.</para>
///
/// <para><strong>A conflicting sale is never resolved automatically, by name or otherwise.</strong>
/// The name step deliberately skips it: a name match would silently pick one of two products that a
/// recorded identifier already disagrees about. It is resolved only by an audited human decision,
/// recorded in <see cref="NayaxSaleProductResolution"/> and applied through the operator command;
/// until then the sale is unresolved, is not costed and is consumed by no replay.</para>
///
/// <para>Append-only evidence: nothing updates or deletes a row here. Whether the conflict has been
/// decided is read from the sale's own <see cref="NayaxSales.ProductId"/>, so the record of what was
/// ambiguous survives the decision that settled it.</para>
/// </summary>
public class NayaxSaleProductConflict : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>The local key of the <see cref="NayaxSales"/> row the conflict is about.</summary>
    public long NayaxSaleId { get; set; }

    /// <summary>
    /// The remote transaction identifier of that sale, copied so an operator can name the sale in
    /// the terms the provider's own reports use.
    /// </summary>
    public long TransactionId { get; set; }

    /// <summary>
    /// The candidate the stored number named directly: the product of this business whose
    /// <c>Product.Id</c> equals it.
    /// </summary>
    public long DirectCandidateProductId { get; set; }

    /// <summary>
    /// The candidate the stored number named as a provider id: the product of this business whose
    /// <c>ExternalProductId</c> is that number.
    /// </summary>
    public long ExternalCandidateProductId { get; set; }

    /// <summary>When the migration recorded the conflict.</summary>
    public DateTime DetectedAtUtc { get; set; }
}
