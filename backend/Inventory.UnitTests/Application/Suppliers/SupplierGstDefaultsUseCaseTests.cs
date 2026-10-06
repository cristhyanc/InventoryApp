using Inventory.Application.Gst;
using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// The supplier GST default use cases (issue #430): a supplier starts with no default of any kind,
/// the three defaults are configured and read back independently of each other, and a value outside
/// the declared vocabulary is refused without storing anything.
///
/// The three stay separate because a supplier may legitimately sell GST-free goods and still charge
/// GST on delivery (parent issue #62), so a test that only set them all at once could not show that
/// a charge default never comes from the product-line default.
/// </summary>
public class SupplierGstDefaultsUseCaseTests
{
    [Fact]
    public async Task A_new_supplier_has_no_configured_defaults()
    {
        var store = new FakeSupplierStore();
        var supplier = await new CreateSupplier(store).Handle("Acme", null, null, null, null, CancellationToken.None);

        var defaults = await new GetSupplierGstDefaults(store).Handle(supplier.Id, CancellationToken.None);

        Assert.Equal(SupplierGstDefaults.None, defaults);
        Assert.Equal(GstRules.None, defaults!.Value.ProductLines);
        Assert.Equal(GstRules.None, defaults.Value.Delivery);
        Assert.Equal(GstRules.None, defaults.Value.Package);
    }

    [Fact]
    public async Task An_unknown_supplier_has_no_defaults_to_read_or_set()
    {
        var store = new FakeSupplierStore();

        Assert.Null(await new GetSupplierGstDefaults(store).Handle(999, CancellationToken.None));
        Assert.Equal(
            GstRuleUpdateOutcome.NotFound,
            (await new SetSupplierGstDefaults(store).Handle(
                999, SupplierGstDefaults.None, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_product_line_delivery_and_package_defaults_are_configured_independently()
    {
        var store = new FakeSupplierStore();
        var supplier = await new CreateSupplier(store).Handle("Acme", null, null, null, null, CancellationToken.None);

        var result = await new SetSupplierGstDefaults(store).Handle(
            supplier.Id,
            new SupplierGstDefaults(
                ProductLines: GstClassification.GstFree,
                Delivery: GstClassification.Taxable,
                Package: GstRules.None),
            CancellationToken.None);

        Assert.Equal(GstRuleUpdateOutcome.Success, result.Outcome);
        var defaults = await new GetSupplierGstDefaults(store).Handle(supplier.Id, CancellationToken.None);
        Assert.Equal(GstClassification.GstFree, defaults!.Value.ProductLines);
        Assert.Equal(GstClassification.Taxable, defaults.Value.Delivery);
        Assert.Equal(GstRules.None, defaults.Value.Package);
    }

    [Theory]
    [InlineData(999, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 3)]
    public async Task An_undefined_default_is_refused_and_stores_nothing(int productLines, int delivery, int package)
    {
        var store = new FakeSupplierStore();
        var supplier = await new CreateSupplier(store).Handle("Acme", null, null, null, null, CancellationToken.None);
        var configured = new SupplierGstDefaults(
            GstClassification.Taxable, GstClassification.Taxable, GstClassification.GstFree);
        await new SetSupplierGstDefaults(store).Handle(supplier.Id, configured, CancellationToken.None);

        var result = await new SetSupplierGstDefaults(store).Handle(
            supplier.Id,
            new SupplierGstDefaults(
                (GstClassification)productLines, (GstClassification)delivery, (GstClassification)package),
            CancellationToken.None);

        Assert.Equal(GstRuleUpdateOutcome.Invalid, result.Outcome);
        Assert.Equal(GstRules.UnsupportedRuleMessage, result.ValidationError);
        Assert.Equal(
            configured,
            await new GetSupplierGstDefaults(store).Handle(supplier.Id, CancellationToken.None));
    }

    /// <summary>
    /// Configuring GST defaults must not disturb the supplier's own details, and editing those
    /// details must not reset the defaults: the two writes own different columns.
    /// </summary>
    [Fact]
    public async Task Configuring_defaults_and_editing_the_supplier_do_not_overwrite_each_other()
    {
        var store = new FakeSupplierStore();
        var supplier = await new CreateSupplier(store).Handle("Acme", "Pat", "555", "a@b.c", "1 Road", CancellationToken.None);
        var configured = new SupplierGstDefaults(
            GstClassification.Taxable, GstClassification.GstFree, GstClassification.Taxable);

        await new SetSupplierGstDefaults(store).Handle(supplier.Id, configured, CancellationToken.None);
        await new UpdateSupplier(store).Handle(supplier.Id, "Acme Renamed", "Pat", "555", "a@b.c", "1 Road", CancellationToken.None);

        var fetched = await new GetSupplier(store).Handle(supplier.Id, CancellationToken.None);
        Assert.Equal("Acme Renamed", fetched!.Name);
        Assert.Equal(configured, await new GetSupplierGstDefaults(store).Handle(supplier.Id, CancellationToken.None));
    }
}
