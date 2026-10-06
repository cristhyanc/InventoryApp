using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// Which stored purchase line each submitted line of an edit refers to (issue #429).
///
/// This matters because a line carries state the request does not resend - its GST classification
/// and that classification's provenance. Matching by product id alone is unambiguous only while a
/// purchase holds at most one line per product; with two lines for the same product, removing or
/// reordering lines would otherwise hand one line's classification to the other.
/// </summary>
public class PurchaseLineIdentityPolicyTests
{
    private const long Coke = 1;
    private const long Chips = 2;

    private static readonly GstClassificationState Taxable =
        new(GstClassification.Taxable, GstClassificationSource.Manual);
    private static readonly GstClassificationState GstFree =
        new(GstClassification.GstFree, GstClassificationSource.Manual);

    [Fact]
    public void An_identified_line_updates_exactly_the_line_it_names()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Coke, GstFree)],
            [Submitted(12, Coke), Submitted(11, Coke)]);

        Assert.Null(match.Error);
        Assert.Equal([12, 11], match.StoredLineIds);
    }

    /// <summary>
    /// The identified line survives while the other is dropped, whichever of the two it is: the
    /// surviving line keeps its own classification rather than inheriting the removed line's.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Removing_one_duplicate_product_line_keeps_the_identity_of_the_other(int survivorId)
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Coke, GstFree)],
            [Submitted(survivorId, Coke)]);

        Assert.Null(match.Error);
        Assert.Equal([survivorId], match.StoredLineIds);
    }

    /// <summary>
    /// The refusal to guess. Two stored lines for one product that disagree about their
    /// classification cannot be told apart by product, so an unidentified submission is rejected
    /// instead of silently carrying one line's classification onto the other.
    /// </summary>
    [Fact]
    public void An_unidentified_line_is_rejected_when_duplicate_product_lines_disagree()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Coke, GstFree)],
            [Submitted(null, Coke)]);

        Assert.Equal(PurchaseLineIdentityPolicy.AmbiguousLineMessage, match.Error);
        Assert.Empty(match.StoredLineIds);
    }

    /// <summary>
    /// Backward compatibility, and the only reason the fallback is safe to keep: duplicate-product
    /// lines that agree about their classification - which is every purchase that existed before
    /// issue #429, all <c>Unknown</c>/<c>Unknown</c> - still match by product in order, because
    /// whichever line is matched carries the same state.
    /// </summary>
    [Fact]
    public void Unidentified_lines_still_match_by_product_when_duplicate_lines_agree()
    {
        var unclassified = GstClassificationState.Unclassified;

        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, unclassified), Stored(12, Coke, unclassified)],
            [Submitted(null, Coke), Submitted(null, Coke)]);

        Assert.Null(match.Error);
        Assert.Equal([11, 12], match.StoredLineIds);
    }

    /// <summary>
    /// One line per product is never ambiguous, so a client that has never heard of line ids keeps
    /// working unchanged even when the lines carry different classifications.
    /// </summary>
    [Fact]
    public void Unidentified_lines_match_by_product_when_each_product_has_one_line()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Chips, GstFree)],
            [Submitted(null, Chips), Submitted(null, Coke)]);

        Assert.Null(match.Error);
        Assert.Equal([12, 11], match.StoredLineIds);
    }

    /// <summary>
    /// An id claims its line before the product fallback runs, so identifying one of two
    /// disagreeing lines makes the other unambiguous rather than leaving the whole request rejected.
    /// </summary>
    [Fact]
    public void An_identified_line_removes_itself_from_the_product_fallback()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Coke, GstFree)],
            [Submitted(12, Coke), Submitted(null, Coke)]);

        Assert.Null(match.Error);
        Assert.Equal([12, 11], match.StoredLineIds);
    }

    [Fact]
    public void A_product_with_no_stored_line_left_is_a_new_line()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable)],
            [Submitted(null, Coke), Submitted(null, Coke), Submitted(null, Chips)]);

        Assert.Null(match.Error);
        Assert.Equal([11, null, null], match.StoredLineIds);
    }

    [Fact]
    public void The_same_line_id_may_not_be_submitted_twice()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable), Stored(12, Coke, GstFree)],
            [Submitted(11, Coke), Submitted(11, Coke)]);

        Assert.Equal(PurchaseLineIdentityPolicy.DuplicateLineIdMessage, match.Error);
        Assert.Empty(match.StoredLineIds);
    }

    /// <summary>
    /// An id this purchase does not hold gets one answer whether it never existed, belongs to
    /// another purchase, or belongs to another business - the caller's purchase is read through the
    /// tenant query filters, so another business's line is simply not among its lines.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void A_line_id_this_purchase_does_not_hold_is_rejected(int foreignId)
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable)],
            [Submitted(foreignId, Coke)]);

        Assert.Equal(PurchaseLineIdentityPolicy.UnknownLineIdMessage, match.Error);
        Assert.Empty(match.StoredLineIds);
    }

    /// <summary>
    /// Re-pointing an identified line at another product would move its classification and its
    /// restock movement into a different product's costing history, which is a different request
    /// than editing that line.
    /// </summary>
    [Fact]
    public void An_identified_line_may_not_change_its_product()
    {
        var match = PurchaseLineIdentityPolicy.Match(
            [Stored(11, Coke, Taxable)],
            [Submitted(11, Chips)]);

        Assert.Equal(PurchaseLineIdentityPolicy.ProductChangedMessage, match.Error);
        Assert.Empty(match.StoredLineIds);
    }

    [Fact]
    public void An_empty_submission_removes_every_stored_line()
    {
        var match = PurchaseLineIdentityPolicy.Match([Stored(11, Coke, Taxable)], []);

        Assert.Null(match.Error);
        Assert.Empty(match.StoredLineIds);
    }

    private static StoredPurchaseLine Stored(int id, long productId, GstClassificationState gst) =>
        new(id, productId, gst);

    private static SubmittedPurchaseLine Submitted(int? id, long productId) => new(id, productId);
}
