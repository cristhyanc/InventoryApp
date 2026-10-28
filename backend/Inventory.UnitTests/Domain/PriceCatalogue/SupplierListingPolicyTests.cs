using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of one supplier listing (issue #923): the pack rule, the barcode rule, and which of a
/// master product or a waiting key a listing belongs to.
///
/// <para>The two rules worth testing hardest are the ones whose wrong answer is invisible. "Units per
/// pack, when known, is a positive integer" means <em>unknown stays unknown</em>: a pack size
/// defaulted to 1 makes a carton's price read as a single's, and defaulted to 0 divides. And a barcode
/// is text: <c>0012345678905</c> parsed as a number is a different barcode, and the lost digits are
/// not recoverable afterwards.</para>
/// </summary>
public class SupplierListingPolicyTests
{
    #region Units per pack

    /// <summary>
    /// Unknown stays unknown. Not 1, not 0, and not guessed from anything: a listing whose pack size
    /// the supplier did not publish has no per-unit price, and that is an answer.
    /// </summary>
    [Fact]
    public void An_unknown_pack_size_stays_unknown() =>
        Assert.Null(SupplierListingPolicy.NormaliseUnitsPerPack(null));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(30)]
    public void A_known_pack_size_is_stored_as_submitted(int unitsPerPack) =>
        Assert.Equal(unitsPerPack, SupplierListingPolicy.NormaliseUnitsPerPack(unitsPerPack));

    /// <summary>
    /// Zero and negative pack sizes are refused rather than read as "unknown". Zero is the dangerous
    /// one: it looks like a recorded fact and it is a division by zero waiting for a per-unit price.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_pack_size_that_is_not_positive_is_refused(int unitsPerPack)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseUnitsPerPack(unitsPerPack));

        Assert.Equal(SupplierListingPolicy.UnitsPerPackNotPositiveMessage, refusal.Message);
    }

    #endregion

    #region Barcode

    /// <summary>
    /// The criterion itself: a barcode keeps its leading zeros. Nothing parses, re-formats, pads or
    /// check-digits it, so the stored value is character for character what the supplier published.
    /// </summary>
    [Fact]
    public void A_barcode_keeps_its_leading_zeros()
    {
        var (barcode, scope) = SupplierListingPolicy.NormaliseBarcode(
            "0012345678905",
            BarcodeScope.Unit);

        Assert.Equal("0012345678905", barcode);
        Assert.Equal(BarcodeScope.Unit, scope);
    }

    /// <summary>
    /// Only the ends are trimmed. Everything inside the code is left exactly as published, because
    /// what a retailer prints is an opaque code rather than a value this application may tidy.
    /// </summary>
    [Fact]
    public void A_barcode_is_trimmed_and_otherwise_untouched()
    {
        var (barcode, _) = SupplierListingPolicy.NormaliseBarcode(
            "  09300675024235  ",
            BarcodeScope.Carton);

        Assert.Equal("09300675024235", barcode);
    }

    /// <summary>No barcode and no scope is the normal state of a listing nobody scanned.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_barcode_and_no_scope_is_accepted(string? barcode)
    {
        var (stored, scope) = SupplierListingPolicy.NormaliseBarcode(barcode, scope: null);

        Assert.Null(stored);
        Assert.Null(scope);
    }

    /// <summary>
    /// A barcode with no scope is refused rather than assumed to be the unit's: a carton code read as
    /// a unit code prices one can at the price of thirty.
    /// </summary>
    [Fact]
    public void A_barcode_without_a_scope_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseBarcode("9300675024235", scope: null));

        Assert.Equal(SupplierListingPolicy.BarcodeScopeRequiredMessage, refusal.Message);
    }

    /// <summary>And a scope with no barcode, which describes nothing.</summary>
    [Fact]
    public void A_scope_without_a_barcode_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseBarcode("  ", BarcodeScope.Carton));

        Assert.Equal(SupplierListingPolicy.BarcodeRequiredForScopeMessage, refusal.Message);
    }

    /// <summary>
    /// An undeclared scope number is refused: an enum is a compile-time constraint and not a
    /// validation, and a deserialized JSON number binds an arbitrary integer.
    /// </summary>
    [Fact]
    public void An_undeclared_barcode_scope_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseBarcode("9300675024235", (BarcodeScope)7));

        Assert.Equal(SupplierListingPolicy.UnknownBarcodeScopeMessage, refusal.Message);
    }

    [Fact]
    public void A_barcode_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseBarcode(
                new string('9', SupplierListingPolicy.MaxBarcodeLength + 1),
                BarcodeScope.Unit));

        Assert.Equal(SupplierListingPolicy.BarcodeTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_barcode_of_exactly_the_limit_is_accepted()
    {
        var longest = new string('9', SupplierListingPolicy.MaxBarcodeLength);

        var (barcode, _) = SupplierListingPolicy.NormaliseBarcode(longest, BarcodeScope.Unit);

        Assert.Equal(longest, barcode);
    }

    #endregion

    #region One owner

    /// <summary>
    /// Exactly one owner: a master product, or the waiting key the search that found the listing was
    /// run for. The combinations are the whole rule, so all four are asserted.
    /// </summary>
    [Theory]
    [InlineData(7L, null, true)]
    [InlineData(null, "COCA-COLACLASSIC|375ML", true)]
    [InlineData(7L, "COCA-COLACLASSIC|375ML", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "   ", false)]
    public void A_listing_belongs_to_exactly_one_of_a_master_product_and_a_waiting_key(
        long? masterProductId,
        string? waitingKey,
        bool accepted) =>
        Assert.Equal(accepted, SupplierListingPolicy.HasExactlyOneOwner(masterProductId, waitingKey));

    [Fact]
    public void A_listing_with_both_owners_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.RequireExactlyOneOwner(7L, "COCACOLA|375ML"));

        Assert.Equal(SupplierListingPolicy.OneOwnerRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_listing_with_neither_owner_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.RequireExactlyOneOwner(masterProductId: null, waitingKey: null));

    #endregion

    #region Supplier product id

    [Fact]
    public void A_supplier_product_id_is_trimmed() =>
        Assert.Equal("SKU-123", SupplierListingPolicy.NormaliseSupplierProductId("  SKU-123 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_listing_without_a_supplier_product_id_is_refused(string? supplierProductId)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseSupplierProductId(supplierProductId));

        Assert.Equal(SupplierListingPolicy.SupplierProductIdRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_supplier_product_id_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPolicy.NormaliseSupplierProductId(
                new string('x', SupplierListingPolicy.MaxSupplierProductIdLength + 1)));

        Assert.Equal(SupplierListingPolicy.SupplierProductIdTooLongMessage, refusal.Message);
    }

    #endregion
}
