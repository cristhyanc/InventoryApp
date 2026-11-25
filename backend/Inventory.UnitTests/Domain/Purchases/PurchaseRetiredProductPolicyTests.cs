using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// Whether a retired product may appear on a purchase (issue #1249, audit #1222 finding INV-14).
///
/// <para><see cref="PurchaseRetiredProductPolicy.NewLineRefusal"/> is asserted here because it is
/// applied twice over for one create - early in <c>UploadPurchase.Handle</c>, so an ordinary
/// submission is refused before a document byte is written, and authoritatively inside
/// <c>EfPurchaseStore.CreateAsync</c>'s write transaction - and both must answer one sentence for one
/// submission.</para>
/// </summary>
public class PurchaseRetiredProductPolicyTests
{
    [Fact]
    public void A_submission_naming_no_retired_product_is_not_refused()
    {
        Assert.Null(PurchaseRetiredProductPolicy.NewLineRefusal(new Dictionary<long, string>()));
    }

    [Fact]
    public void A_submission_naming_a_retired_product_is_refused_by_name()
    {
        Assert.Equal(
            PurchaseRetiredProductPolicy.NewLineRefusalMessage("Coke Zero"),
            PurchaseRetiredProductPolicy.NewLineRefusal(new Dictionary<long, string> { [7] = "Coke Zero" }));
    }

    [Fact]
    public void Several_retired_products_are_refused_with_one_sentence_naming_the_lowest_id()
    {
        // Enumeration order of a dictionary is not something to rest a message on, so the product
        // named is chosen rather than taken: the lowest id, so one submission always produces the
        // same sentence however the read happened to return its rows.
        var retired = new Dictionary<long, string> { [9] = "Chips", [4] = "Water", [6] = "Coke Zero" };

        Assert.Equal(
            PurchaseRetiredProductPolicy.NewLineRefusalMessage("Water"),
            PurchaseRetiredProductPolicy.NewLineRefusal(retired));
    }

    [Theory]
    [InlineData(10, 11, false)]
    [InlineData(10, 10, true)]
    [InlineData(10, 4, true)]
    [InlineData(10, 0, true)]
    public void Only_raising_an_existing_lines_quantity_is_refused(
        decimal stored, decimal requested, bool allowed)
    {
        Assert.Equal(allowed, PurchaseRetiredProductPolicy.IsExistingLineQuantityAllowed(stored, requested));
    }
}
