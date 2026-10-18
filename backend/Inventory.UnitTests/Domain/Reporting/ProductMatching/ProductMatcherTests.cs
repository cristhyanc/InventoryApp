using Inventory.Domain.Reporting.ProductMatching;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.ProductMatching;

/// <summary>
/// <see cref="ProductMatcher.MatchLinked"/> is the whole matching rule, and its id argument is a
/// <strong>local</strong> product id - the one a Nayax id resolved to through
/// <c>Product.ExternalProductId</c> (issues #745 and #759), or <see langword="null"/> when the
/// business has that Nayax id on no product.
///
/// Issue #735 (A4) removed the Nayax-id overload this class used to exercise beside it: with a new
/// product's key assigned by the database, matching a provider's number against <c>Product.Id</c>
/// resolves to whichever row happens to hold that number. Every rule that overload covered - id
/// first, then the unambiguous normalized name - is covered here, over the local id, because it is
/// the same rule applied to a resolved identity.
/// </summary>
public class ProductMatcherTests
{
    private static readonly ProductMatchCandidate[] Catalogue =
    [
        new(1, "Nu Pure Spring Water 600mL"),
        new(2, "Maltese King Share 60g"),
        new(3, "Ambiguous Snack")
    ];

    [Fact]
    public void Matches_the_linked_local_product_and_ignores_the_raw_name()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, 1, "Some other raw Nayax name");

        Assert.Equal(1, matched);
    }

    /// <summary>
    /// An id the catalogue does not hold is an unknown identity, so the name rule decides - with the
    /// Nayax price/code suffix stripped.
    /// </summary>
    [Fact]
    public void A_linked_id_the_catalogue_does_not_hold_falls_back_to_the_normalized_name()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, 999, "Maltese King Share 60g(29, 29 = 4.80)");

        Assert.Equal(2, matched);
    }

    [Fact]
    public void No_linked_id_falls_back_to_the_normalized_name()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, null, "Nu Pure Spring Water 600mL (999)");

        Assert.Equal(1, matched);
    }

    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, null, "nu pure spring water 600ml");

        Assert.Equal(1, matched);
    }

    /// <summary>
    /// Nothing to match on either side: an id the catalogue does not hold, or none at all, with a
    /// name that is blank, absent or held by no product.
    /// </summary>
    [Fact]
    public void An_unmatched_id_with_no_usable_name_is_unmapped()
    {
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, 999, "   "));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, 999, null));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, null));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, "   "));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, "A product this catalogue does not hold"));
    }

    [Fact]
    public void Ambiguous_name_match_across_two_or_more_candidates_is_unmapped()
    {
        var duplicateNameCatalogue = new List<ProductMatchCandidate>(Catalogue)
        {
            new(4, "Ambiguous Snack")
        };

        var matched = ProductMatcher.MatchLinked(duplicateNameCatalogue, null, "Ambiguous Snack");

        Assert.Null(matched);
    }

    [Theory]
    [InlineData("Water (500)", "Water")]
    [InlineData("Water", "Water")]
    [InlineData("  Water  (500)", "Water")]
    public void NormalizeName_strips_the_parenthesis_suffix_and_trims(string raw, string expected)
    {
        Assert.Equal(expected, ProductMatcher.NormalizeName(raw));
    }
}
