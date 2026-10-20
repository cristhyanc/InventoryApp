using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The fingerprint that makes a batch refill's idempotency key mean something (issue #743): the
/// same submission has to render identically so a retry is recognised, and anything a caller could
/// change about what is deducted has to render differently so a reused key is refused rather than
/// recording a second pick.
/// </summary>
public class PickListRefillFingerprintTests
{
    private static string Compute(
        long? machineId = null, string? notes = null, params (long ProductId, int Quantity)[] rows) =>
        PickListRefillFingerprint.Compute(
            machineId, notes, [.. rows.Select(row => new PickListRefillRequestRow(row.ProductId, row.Quantity))]);

    [Fact]
    public void The_same_request_renders_the_same_fingerprint() =>
        Assert.Equal(
            Compute(7, "Friday run", (10, 3), (11, 2)),
            Compute(7, "Friday run", (10, 3), (11, 2)));

    /// <summary>
    /// Row order is not part of the request: the same products and quantities typed in another
    /// order are the same pick, so a retry whose rows arrive reordered is still a retry.
    /// </summary>
    [Fact]
    public void Row_order_does_not_change_the_fingerprint() =>
        Assert.Equal(
            Compute(null, null, (10, 3), (11, 2)),
            Compute(null, null, (11, 2), (10, 3)));

    [Fact]
    public void A_different_quantity_changes_the_fingerprint() =>
        Assert.NotEqual(
            Compute(null, null, (10, 3)),
            Compute(null, null, (10, 4)));

    [Fact]
    public void A_different_product_changes_the_fingerprint() =>
        Assert.NotEqual(
            Compute(null, null, (10, 3)),
            Compute(null, null, (11, 3)));

    [Fact]
    public void An_extra_row_changes_the_fingerprint() =>
        Assert.NotEqual(
            Compute(null, null, (10, 3)),
            Compute(null, null, (10, 3), (11, 1)));

    [Fact]
    public void A_different_machine_changes_the_fingerprint() =>
        Assert.NotEqual(
            Compute(7, null, (10, 3)),
            Compute(8, null, (10, 3)));

    [Fact]
    public void Naming_no_machine_is_not_the_same_as_naming_one() =>
        Assert.NotEqual(
            Compute(null, null, (10, 3)),
            Compute(7, null, (10, 3)));

    [Fact]
    public void A_different_note_changes_the_fingerprint() =>
        Assert.NotEqual(
            Compute(null, "Friday run", (10, 3)),
            Compute(null, "Saturday run", (10, 3)));

    [Fact]
    public void No_note_is_not_the_same_as_a_note() =>
        Assert.NotEqual(
            Compute(null, null, (10, 3)),
            Compute(null, "Friday run", (10, 3)));

    /// <summary>
    /// A note cannot borrow the canonical rendering's own separators to make two different requests
    /// render the same way, which is why its length is rendered with it.
    /// </summary>
    [Fact]
    public void A_note_cannot_impersonate_the_rendering() =>
        Assert.NotEqual(
            Compute(null, "a|b", (10, 3)),
            Compute(null, "a\nb", (10, 3)));

    [Fact]
    public void An_empty_request_renders_rather_than_throwing() =>
        Assert.NotEmpty(Compute());

    /// <summary>
    /// Lowercase hexadecimal SHA-256, so the stored column's length is known and a value is
    /// comparable byte for byte.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_lowercase_hexadecimal_sha256()
    {
        var fingerprint = Compute(7, "Friday run", (10, 3));

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789abcdef"));
    }
}
