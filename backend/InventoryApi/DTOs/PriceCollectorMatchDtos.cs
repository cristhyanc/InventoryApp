using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;

namespace InventoryApi.DTOs;

/// <summary>
/// One match proposal the collector submits (issue #941, parent #817).
///
/// <para><strong>It names a waiting key and nothing a business owns.</strong> The key is the one the
/// work list published; there is no business id, no product id and no mapping id in this contract,
/// because a proposal is an opinion about a <em>product description</em>. Every enum travels as its
/// number, as every other enum this API publishes does, and a proposal carries either a master product
/// id or a complete description of a new one - never both.</para>
/// </summary>
public sealed record PriceCollectorMatchProposalRequest
{
    /// <summary>
    /// The waiting key this proposal is about, as <c>GET /api/price-collector/work</c> published it.
    ///
    /// <para>Deliberately bounded by nothing here: a key is only ever <em>compared</em> with the keys
    /// the run was issued work for, and anything that is not one of them - a key that is too long
    /// included - is refused with <c>unknown waiting key</c>. The key that reaches a stored row is
    /// therefore always one this database issued, which is what makes a length attribute on the way in
    /// a second answer rather than a safeguard.</para>
    /// </summary>
    public string? WaitingKey { get; init; }

    /// <summary>The existing master product being proposed, or left out when a new one is described.</summary>
    public long? MasterProductId { get; init; }

    /// <summary>The brand of a new master product being suggested.</summary>
    [MaxLength(MasterProductPolicy.MaxBrandLength)]
    public string? SuggestedBrand { get; init; }

    /// <summary>Its name or variant.</summary>
    [MaxLength(MasterProductPolicy.MaxNameLength)]
    public string? SuggestedName { get; init; }

    /// <summary>The size of one individual item.</summary>
    public decimal? SuggestedSizeValue { get; init; }

    /// <summary>What that size is measured in.</summary>
    public MasterProductSizeUnit? SuggestedSizeUnit { get; init; }

    /// <summary>
    /// How sure the matcher is: <c>high</c>, <c>medium</c> or <c>low</c> as their declared numbers.
    /// Marked <see cref="JsonRequiredAttribute"/> because a confidence left out would otherwise default
    /// to the enum's own <c>0</c>, which names none of the three levels - and "no answer" must not read
    /// as the level that applies a match.
    /// </summary>
    [JsonRequired]
    public ProductMatchConfidence Confidence { get; init; }

    /// <summary>The matcher's own short reason, which is the evidence a reviewer reads.</summary>
    [MaxLength(ProductMatchSuggestionPolicy.MaxReasonLength)]
    public string? Reason { get; init; }

    /// <summary>This proposal as the use case takes it.</summary>
    public ProductMatchProposal ToProposal() =>
        new(
            WaitingKey,
            MasterProductId,
            SuggestedBrand,
            SuggestedName,
            SuggestedSizeValue,
            SuggestedSizeUnit,
            Confidence,
            Reason);
}

/// <summary>
/// One submitted batch of proposals (issue #941): the run they belong to and the proposals, at most
/// <c>AutomaticProductMatchPolicy.MaxProposalsPerBatch</c> of them.
/// </summary>
public sealed record PriceCollectorMatchesRequest
{
    /// <summary>
    /// The run the collector is reporting under, as the work list issued it. Marked
    /// <see cref="JsonRequiredAttribute"/> because a run id defaults to <c>0</c> on under-posting,
    /// which is never a real run rather than being refused outright.
    /// </summary>
    [JsonRequired]
    public long RunId { get; init; }

    /// <summary>The proposals. A larger batch is refused whole, with nothing stored.</summary>
    public IReadOnlyList<PriceCollectorMatchProposalRequest> Proposals { get; init; } = [];

    /// <summary>This batch as the use case takes it.</summary>
    public ProductMatchBatch ToBatch() =>
        new(RunId, [.. Proposals.Select(proposal => proposal.ToProposal())]);
}

/// <summary>What became of one proposal, as the response reports it (issue #941).</summary>
public sealed record PriceCollectorMatchResultResponse(
    string WaitingKey,
    ProductMatchProposalOutcome Outcome,
    string? Reason,
    long? MasterProductId,
    long? SuggestionId)
{
    public static PriceCollectorMatchResultResponse From(ProductMatchProposalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PriceCollectorMatchResultResponse(
            result.WaitingKey,
            result.Outcome,
            result.Reason,
            result.MasterProductId,
            result.SuggestionId);
    }
}

/// <summary>
/// The answer to one submitted batch of proposals (issue #941): the proposals in three lists, which
/// are the three outcomes, each carrying the reason it got that one.
///
/// <para>Nothing here says anything about a business - not an id, not a name, not a product, and not a
/// count of the mappings a match moved, which would be a count of the businesses tracking the product.
/// A waiting key, a master product id and a suggestion id are facts about product descriptions, which
/// is what lets this surface be reachable by a caller that holds no membership anywhere.</para>
/// </summary>
public sealed record PriceCollectorMatchesResponse(
    long RunId,
    IReadOnlyList<PriceCollectorMatchResultResponse> AutoMatched,
    IReadOnlyList<PriceCollectorMatchResultResponse> Tentative,
    IReadOnlyList<PriceCollectorMatchResultResponse> Rejected)
{
    public static PriceCollectorMatchesResponse From(ProductMatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new PriceCollectorMatchesResponse(
            receipt.RunId,
            [.. receipt.AutoMatched.Select(PriceCollectorMatchResultResponse.From)],
            [.. receipt.Tentative.Select(PriceCollectorMatchResultResponse.From)],
            [.. receipt.Rejected.Select(PriceCollectorMatchResultResponse.From)]);
    }
}
