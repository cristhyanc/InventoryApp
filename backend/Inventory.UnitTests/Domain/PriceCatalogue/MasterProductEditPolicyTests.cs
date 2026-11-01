using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of creating and correcting one master product from the Master Products page (issue
/// #1024, parent #817): what counts as a change of <em>size</em>, when such a change is refused, and
/// when an edit is answering a page that has moved on.
///
/// <para>The safeguard is the one that matters here, and it is financial rather than cosmetic: a
/// confirmed supplier listing's pack size was confirmed against the size this row holds, and every
/// tracking business's per-unit comparison divides by it. Changing 375 ml to 600 ml under a confirmed
/// 30-pack would silently re-price that pack for everybody, so it is refused while any confirmed
/// listing exists.</para>
/// </summary>
public class MasterProductEditPolicyTests
{
    private static readonly DateTime Stored = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    #region What is a size change

    /// <summary>
    /// Three equal decimals with three spellings are one size: the stored scale is three decimal
    /// places and <c>375</c>, <c>375.0</c> and <c>375.000</c> are the same number, so re-submitting a
    /// size with trailing zeros is not an edit of it and must not be refused as one.
    /// </summary>
    [Fact]
    public void The_same_size_in_another_spelling_is_not_a_change() =>
        Assert.All(
            // Written as decimal literals rather than theory data: the three differ only in scale,
            // which is exactly the property under test and is what an attribute argument would lose.
            new[] { 375m, 375.0m, 375.000m },
            submitted => Assert.False(MasterProductEditPolicy.IsSizeChange(
                375m,
                MasterProductSizeUnit.Millilitre,
                submitted,
                MasterProductSizeUnit.Millilitre)));

    /// <summary>
    /// A different value is a change, and so is a different unit at the same value: 1 l and 1 each
    /// are not one size, and the unit is the whole of what makes a size comparable.
    /// </summary>
    [Theory]
    [InlineData(600, MasterProductSizeUnit.Millilitre)]
    [InlineData(375, MasterProductSizeUnit.Gram)]
    [InlineData(1, MasterProductSizeUnit.Litre)]
    public void A_different_value_or_unit_is_a_change(decimal value, MasterProductSizeUnit unit) =>
        Assert.True(MasterProductEditPolicy.IsSizeChange(
            375m,
            MasterProductSizeUnit.Millilitre,
            value,
            unit));

    #endregion

    #region The confirmed-listing safeguard

    /// <summary>
    /// The issue's own criterion: a size change is refused while the product has any confirmed
    /// supplier listing, and the refusal says why rather than only that it was refused.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void A_size_change_is_refused_while_a_confirmed_listing_exists(int confirmed)
    {
        var refusal = Assert.Throws<DomainConflictException>(
            () => MasterProductEditPolicy.RequireSizeMayChange(sizeChanged: true, confirmed));

        Assert.Equal(MasterProductEditPolicy.SizeChangeRefusedMessage, refusal.Message);
    }

    /// <summary>With no confirmed listing there is no confirmed pack size to invalidate.</summary>
    [Fact]
    public void A_size_change_is_allowed_with_no_confirmed_listing() =>
        MasterProductEditPolicy.RequireSizeMayChange(sizeChanged: true, confirmedListingCount: 0);

    /// <summary>
    /// A brand or name edit is always allowed, confirmed listings or not: the brand and the name are
    /// the shared catalogue's own label for the product and no pack price was ever divided by either.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void An_edit_that_does_not_change_the_size_is_always_allowed(int confirmed) =>
        MasterProductEditPolicy.RequireSizeMayChange(sizeChanged: false, confirmed);

    #endregion

    #region Stale edits

    /// <summary>
    /// An edit presents the value it read, and a value that is not the row's current one means the
    /// row moved since: two Super Admin tabs cannot overwrite each other silently.
    /// </summary>
    [Fact]
    public void An_edit_presenting_another_instant_is_refused_as_stale()
    {
        var refusal = Assert.Throws<DomainConflictException>(
            () => MasterProductEditPolicy.RequireCurrent(Stored.AddSeconds(-1), Stored));

        Assert.Equal(MasterProductEditPolicy.StaleEditMessage, refusal.Message);
    }

    /// <summary>The value the row actually holds is what lets the edit through.</summary>
    [Fact]
    public void An_edit_presenting_the_stored_instant_is_current() =>
        MasterProductEditPolicy.RequireCurrent(Stored, Stored);

    /// <summary>
    /// An edit that presents nothing at all is a <em>validation</em> failure rather than a conflict:
    /// the request is incomplete, not stale, and the two answers must stay different because one of
    /// them tells the page to re-read and the other tells it to send the field.
    /// </summary>
    [Fact]
    public void An_edit_presenting_nothing_is_incomplete()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => MasterProductEditPolicy.RequireExpectedUpdatedAt(null));

        Assert.Equal(MasterProductEditPolicy.ExpectedUpdatedAtRequiredMessage, refusal.Message);
    }

    [Fact]
    public void An_edit_presenting_an_instant_keeps_it() =>
        Assert.Equal(Stored, MasterProductEditPolicy.RequireExpectedUpdatedAt(Stored));

    #endregion
}
