namespace Inventory.Application.Nayax;

/// <summary>
/// The one way a Nayax product identifier becomes a local product identifier (issue #733, parent
/// #329 decision A2).
///
/// <para><strong>A Nayax id is not a local id.</strong> Every feature that reads live Nayax machine
/// data gets <c>NayaxProductID</c> values back - the Lynx contract's "unique identifier of the
/// product in the Nayax system" (<c>GET /v1/machines/{MachineID}/machineProducts</c>,
/// <c>integer&lt;int64&gt;</c>, nullable). Those features used to compare that value with
/// <c>Product.Id</c> directly, which only worked because the local key happened to be the Nayax id.
/// Issue #732 put the mapping in the <c>ProductExternalIds</c> link table instead; this port is how
/// it is read, so a Nayax id is translated once, at the edge where Nayax data enters the
/// application, and every persistence port downstream of it speaks local product ids only.</para>
///
/// <para><strong>No link means no product.</strong> A Nayax id this business holds no link for
/// names nothing locally, and must be treated exactly as an unknown product already is in each
/// path - never matched to the product whose local id happens to be the same number.</para>
/// </summary>
public interface INayaxProductLinkStore
{
    /// <summary>
    /// The local product id for each of the given Nayax product ids that the current business has a
    /// Nayax link for, keyed by the Nayax id. Ids with no link are absent from the result rather
    /// than mapped to themselves, and an empty request reads nothing.
    ///
    /// <para>The result is scoped to the caller's business by the ordinary tenant query filters, and
    /// a link cannot point outside its own business by a database constraint, so a Nayax id another
    /// business uses resolves to nothing here.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, long>> GetLocalProductIdsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken);
}
