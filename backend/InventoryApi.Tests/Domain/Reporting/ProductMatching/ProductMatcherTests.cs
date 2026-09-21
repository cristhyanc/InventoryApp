using System.Collections.Generic;
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
}
