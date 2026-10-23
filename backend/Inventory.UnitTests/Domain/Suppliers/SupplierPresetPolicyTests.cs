using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Xunit;

namespace Inventory.UnitTests.Domain.Suppliers;

/// <summary>
/// The platform supplier preset's own rules (issue #848): the name is required, trimmed and
/// bounded, and two names are compared with case ignored through the value the unique index is
/// over.
/// </summary>
public class SupplierPresetPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_preset_needs_a_name(string? name)
    {
        var error = Assert.Throws<DomainValidationException>(() => SupplierPresetPolicy.NormaliseName(name));

        Assert.Equal(SupplierPresetPolicy.NameRequiredMessage, error.Message);
    }

    [Theory]
    [InlineData("Coles", "Coles")]
    [InlineData("  Big W  ", "Big W")]
    [InlineData("\tCostco\n", "Costco")]
    public void The_stored_name_is_the_trimmed_one_with_its_capitalisation_kept(string name, string expected) =>
        Assert.Equal(expected, SupplierPresetPolicy.NormaliseName(name));

    [Fact]
    public void The_name_length_limit_is_measured_on_the_trimmed_value()
    {
        var atTheLimit = new string('n', SupplierPresetPolicy.MaxNameLength);
        Assert.Equal(atTheLimit, SupplierPresetPolicy.NormaliseName($"  {atTheLimit}  "));

        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetPolicy.NormaliseName(new string('n', SupplierPresetPolicy.MaxNameLength + 1)));

        Assert.Equal(SupplierPresetPolicy.NameTooLongMessage, error.Message);
    }

    /// <summary>
    /// The comparison the unique index is over. Trimming is part of it, so a name that differs only
    /// in surrounding whitespace is the same suggestion.
    /// </summary>
    [Theory]
    [InlineData("Coles", "COLES")]
    [InlineData("coles", "COLES")]
    [InlineData("  CoLeS  ", "COLES")]
    [InlineData("Big W", "BIG W")]
    public void Two_names_that_differ_only_in_case_or_padding_share_one_key(string name, string expected) =>
        Assert.Equal(expected, SupplierPresetPolicy.NormalisedName(name));

    [Theory]
    [InlineData("Coles", "coles")]
    [InlineData("Coles", "  COLES")]
    [InlineData("Woolworths", "woolWORTHS")]
    public void A_duplicate_is_recognised_whatever_the_caller_typed(string stored, string submitted) =>
        Assert.Equal(
            SupplierPresetPolicy.NormalisedName(stored),
            SupplierPresetPolicy.NormalisedName(submitted));

    [Theory]
    [InlineData("Coles", "Costco")]
    [InlineData("Big W", "BigW")]
    public void Two_genuinely_different_names_do_not_collide(string first, string second) =>
        Assert.NotEqual(
            SupplierPresetPolicy.NormalisedName(first),
            SupplierPresetPolicy.NormalisedName(second));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_blank_name_normalises_to_an_empty_key_rather_than_throwing(string? name) =>
        Assert.Equal(string.Empty, SupplierPresetPolicy.NormalisedName(name));

    /// <summary>
    /// The preset's URL is the shared rule rather than a second copy of it, which is what makes the
    /// value issue #847 stores and the value copied into a business the same value.
    /// </summary>
    [Fact]
    public void The_preset_url_is_the_shared_supplier_url_rule()
    {
        Assert.Null(SupplierPresetPolicy.NormaliseUrl("  "));
        Assert.Equal("https://www.coles.com.au", SupplierPresetPolicy.NormaliseUrl(" https://www.coles.com.au "));

        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetPolicy.NormaliseUrl("www.coles.com.au"));

        Assert.Equal(SupplierUrlPolicy.InvalidMessage, error.Message);
    }
}
