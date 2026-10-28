using Inventory.Domain.PriceCatalogue;

namespace InventoryApi.DTOs;

/// <summary>
/// The body of <c>PUT /api/products/{id}/price-tracking</c> (issue #938): the state the
/// "Track supplier prices" switch is being put into, and nothing else.
///
/// <para>One field, deliberately. The search text is the product's own name and unit label read by
/// the server, the status of a new mapping is always <c>Requested</c>, and no master product or match
/// source is submittable at all - deciding what a product <em>is</em> belongs to the automatic matcher
/// (#941) and the Super Admin review (#928). An omitted <c>tracked</c> binds to <c>false</c>, which is
/// "turn it off", exactly as sending it explicitly would.</para>
/// </summary>
public record ProductPriceTrackingDto(bool Tracked);

/// <summary>
/// The matched master product, as a business endpoint publishes it (issue #938): its name and its
/// size, and nothing else at all.
///
/// <para><strong>This is the reviewed limit of a platform-table read, not a convenience
/// projection.</strong> The master products are shared across every business, so what a business
/// endpoint may say about one was decided on issue #938 itself: only a matched mapping's master
/// product, and only its name and size. The brand, the row key, the normalised key, the instants and
/// every supplier listing beside it are unreachable here, and widening this is a tenancy decision
/// needing its own reviewed issue (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
/// <param name="Name">The master product's name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">
/// The code that size is measured in - <c>ml</c>, <c>l</c>, <c>g</c>, <c>kg</c> or <c>each</c>. The
/// published code rather than the enum member name, so what a client renders is the vocabulary
/// <c>MasterProductPolicy.SizeUnitCode</c> defines rather than a programmer's label.
/// </param>
public record MatchedMasterProductResponse(string Name, decimal SizeValue, string SizeUnit);

/// <summary>
/// Where one product has got to in the shared supplier price catalogue, as
/// <c>GET /api/products/{id}/price-tracking</c> answers it (issue #938).
///
/// <para><see cref="CanTrack"/> and <see cref="TrackRefusal"/> are the server's own eligibility
/// answer about the stored product - <c>ProductPriceTrackingPolicy.TrackingRefusal</c>, the same rule
/// the write applies - so the switch and the message beside it never word the rule a second time. It
/// is the shape the product's own <c>canDelete</c>/<c>deletionRefusal</c> already has.</para>
///
/// <para>Nothing here says anything about any other business. There is nowhere for such a value to
/// travel: a master product is a fact about a brand's product and carries no business identity, and
/// the mapping half of the answer is this business's own row.</para>
/// </summary>
/// <param name="ProductId">The product this answer is about.</param>
/// <param name="Tracked">Whether this business has asked for the product's supplier prices.</param>
/// <param name="Status">
/// Where the mapping has got to - requested, auto-matched, matched or not found - or
/// <see langword="null"/> when the product is not tracked.
/// </param>
/// <param name="SearchName">
/// The product name the collector searches with, or <see langword="null"/> when untracked. It is the
/// product's own name as it was captured, which is what makes "the search text was copied and is
/// refreshed while awaiting a match" readable rather than inferred.
/// </param>
/// <param name="UnitLabel">The unit label beside it, or <see langword="null"/> when untracked.</param>
/// <param name="MasterProduct">
/// The matched master product's name and size, or <see langword="null"/> whenever the mapping is not
/// in one of the two matched states.
/// </param>
/// <param name="CanTrack">Whether tracking may be turned on for this product as it now stands.</param>
/// <param name="TrackRefusal">
/// Why it may not be, in the rule's own words - today a blank or generic unit label - or
/// <see langword="null"/> when it may.
/// </param>
public record ProductPriceTrackingResponse(
    long ProductId,
    bool Tracked,
    ProductPriceTrackingStatus? Status,
    string? SearchName,
    string? UnitLabel,
    MatchedMasterProductResponse? MasterProduct,
    bool CanTrack,
    string? TrackRefusal);

/// <summary>
/// One tracked product in <c>GET /api/products/price-tracking</c> (issue #938), which is what the
/// catalogue listing's "Tracked" column is built from.
///
/// <para>The product id and the status, and nothing else: the column says which products are tracked
/// and where each one has got to. It is its own resource rather than two fields on the product
/// payload because that payload - and every nested product snapshot a purchase or supplier-order
/// response carries - is a pinned API contract, the same reason the product's GST rule is its own
/// resource (docs/architecture.md § Product and supplier GST rules).</para>
/// </summary>
/// <param name="ProductId">The business's own product.</param>
/// <param name="Status">Where its mapping has got to.</param>
public record TrackedProductResponse(long ProductId, ProductPriceTrackingStatus Status);
