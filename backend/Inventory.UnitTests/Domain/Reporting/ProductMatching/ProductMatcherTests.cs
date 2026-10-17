using Inventory.Domain.Reporting.ProductMatching;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.ProductMatching;

public class ProductMatcherTests
{
    private static readonly ProductMatchCandidate[] Catalogue =
    [
        new(1, "Nu Pure Spring Water 600mL"),
        new(2, "Maltese King Share 60g"),
        new(3, "Ambiguous Snack")
    ];

    [Fact]
    public void Matches_by_id_when_the_id_is_in_the_catalogue()
    {
        var matched = ProductMatcher.Match(Catalogue, 1, "Some other raw Nayax name");

        Assert.Equal(1, matched);
    }

    [Fact]
    public void Falls_back_to_normalized_name_when_id_is_unmatched()
    {
        var matched = ProductMatcher.Match(Catalogue, 999, "Maltese King Share 60g(29, 29 = 4.80)");

        Assert.Equal(2, matched);
    }

    [Fact]
    public void Falls_back_to_normalized_name_when_id_is_null()
    {
        var matched = ProductMatcher.Match(Catalogue, null, "Nu Pure Spring Water 600mL (999)");

        Assert.Equal(1, matched);
    }

    [Fact]
    public void Unknown_id_and_blank_name_is_unmapped()
    {
        var matched = ProductMatcher.Match(Catalogue, 999, "   ");

        Assert.Null(matched);
    }

    [Fact]
    public void Unknown_id_and_null_name_is_unmapped()
    {
        var matched = ProductMatcher.Match(Catalogue, 999, null);

        Assert.Null(matched);
    }

    [Fact]
    public void Ambiguous_name_match_across_two_or_more_candidates_is_unmapped()
    {
        var duplicateNameCatalogue = new List<ProductMatchCandidate>(Catalogue)
        {
            new(4, "Ambiguous Snack")
        };

        var matched = ProductMatcher.Match(duplicateNameCatalogue, null, "Ambiguous Snack");

        Assert.Null(matched);
    }

    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var matched = ProductMatcher.Match(Catalogue, null, "nu pure spring water 600ml");

        Assert.Equal(1, matched);
    }

    [Theory]
    [InlineData("Water (500)", "Water")]
    [InlineData("Water", "Water")]
    [InlineData("  Water  (500)", "Water")]
    public void NormalizeName_strips_the_parenthesis_suffix_and_trims(string raw, string expected)
    {
        Assert.Equal(expected, ProductMatcher.NormalizeName(raw));
    }

    /// <summary>
    /// <see cref="ProductMatcher.MatchLinked"/> (issue #745) takes the local product id a Nayax link
    /// already resolved, so the identity step is the only thing that changed: the linked catalogue
    /// row wins and short-circuits the name fallback, an id no link resolved falls back to the name,
    /// and a catalogue row is never reached because its own id equals a Nayax id.
    /// </summary>
    [Fact]
    public void Matches_the_linked_local_product_and_ignores_the_raw_name()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, 1, "Some other raw Nayax name");

        Assert.Equal(1, matched);
    }

    [Fact]
    public void An_unlinked_nayax_id_falls_back_to_the_normalized_name()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, null, "Maltese King Share 60g(29, 29 = 4.80)");

        Assert.Equal(2, matched);
    }

    [Fact]
    public void An_unlinked_nayax_id_with_no_usable_name_is_unmapped()
    {
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, null));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, "   "));
        Assert.Null(ProductMatcher.MatchLinked(Catalogue, null, "A product this catalogue does not hold"));
    }

    /// <summary>
    /// The link is the identity: a local id the link table did not name is not matched even when the
    /// Nayax id and that local id are the same number, which is what makes the coincidence
    /// <c>Product.Id</c> currently has harmless once #735 (A4) ends it.
    /// </summary>
    [Fact]
    public void A_linked_local_id_the_catalogue_does_not_hold_is_unmapped()
    {
        var matched = ProductMatcher.MatchLinked(Catalogue, 999, null);

        Assert.Null(matched);
    }
}
