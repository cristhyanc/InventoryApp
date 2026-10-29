using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Domain.SupplierOrders;

/// <summary>
/// The stale-decision guard (issue #974). Anything that changes the balance an operator decided over
/// has to change the fingerprint, and anything that does not must leave it alone - otherwise either
/// a competing receipt slips through, or an honest save is refused for no reason.
/// </summary>
public class SupplierOrderCompletionFingerprintTests
{
    private static readonly SupplierOrderLineFulfillment[] Lines =
    [
        new(11, 24m, 6m),
        new(12, 10m, 0m),
    ];

    private static string Compute(
        int businessId = 1,
        int orderId = 9,
        SupplierOrderStatus status = SupplierOrderStatus.PartiallyReceived,
        IEnumerable<SupplierOrderLineFulfillment>? lines = null) =>
        SupplierOrderCompletionFingerprint.Compute(businessId, orderId, status, lines ?? Lines);

    [Fact]
    public void The_same_order_renders_the_same_fingerprint() => Assert.Equal(Compute(), Compute());

    [Fact]
    public void The_fingerprint_is_lowercase_hexadecimal_sha256()
    {
        var fingerprint = Compute();

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>A competing receipt is the case this exists for.</summary>
    [Fact]
    public void A_receipt_landing_in_between_changes_the_fingerprint() =>
        Assert.NotEqual(Compute(), Compute(lines: [new(11, 24m, 10m), new(12, 10m, 0m)]));

    [Fact]
    public void A_changed_ordered_quantity_changes_the_fingerprint() =>
        Assert.NotEqual(Compute(), Compute(lines: [new(11, 30m, 6m), new(12, 10m, 0m)]));

    [Fact]
    public void A_cancelled_or_completed_order_changes_the_fingerprint()
    {
        Assert.NotEqual(Compute(), Compute(status: SupplierOrderStatus.Cancelled));
        Assert.NotEqual(Compute(), Compute(status: SupplierOrderStatus.ClosedPartiallyDelivered));
        Assert.NotEqual(Compute(), Compute(status: SupplierOrderStatus.Received));
    }

    [Fact]
    public void A_line_added_or_removed_changes_the_fingerprint()
    {
        Assert.NotEqual(Compute(), Compute(lines: [new(11, 24m, 6m)]));
        Assert.NotEqual(Compute(), Compute(lines: [.. Lines, new SupplierOrderLineFulfillment(13, 5m, 0m)]));
    }

    /// <summary>Another business's decision is refused even when the two orders would render identically.</summary>
    [Fact]
    public void The_owning_business_is_part_of_the_rendering() => Assert.NotEqual(Compute(), Compute(businessId: 2));

    [Fact]
    public void Another_order_of_the_same_business_renders_differently() =>
        Assert.NotEqual(Compute(), Compute(orderId: 10));

    /// <summary>
    /// The quantities are stored as <c>decimal(18,4)</c>, so a re-read value comes back at a
    /// different scale. That is the same value, not a change, and must not refuse an honest save.
    /// </summary>
    [Fact]
    public void A_quantity_re_read_at_the_columns_scale_renders_identically() =>
        Assert.Equal(Compute(), Compute(lines: [new(11, 24.0000m, 6.0000m), new(12, 10.0000m, 0.0000m)]));

    [Fact]
    public void The_order_the_lines_arrive_in_does_not_matter() =>
        Assert.Equal(Compute(), Compute(lines: [Lines[1], Lines[0]]));
}
