using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The canonical rendering behind a multi-machine batch's idempotency key (issue #897). What it has
/// to get right is the difference between a retry and a different pick: the same allocations however
/// they were typed are one submission, and anything else is not.
/// </summary>
public class MultiMachinePickListRefillFingerprintTests
{
    private const long MachineA = 7;
    private const long MachineB = 8;

    private static string Compute(string? notes, params (long MachineId, long ProductId, int Quantity)[] lines) =>
        MultiMachinePickListRefillFingerprint.Compute(
            notes,
            [.. lines.Select(line => new MultiMachineRefillLine(line.MachineId, line.ProductId, line.Quantity))]);

    [Fact]
    public void The_fingerprint_is_lowercase_hexadecimal_sha256()
    {
        var fingerprint = Compute(null, (MachineA, 100, 2));

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>
    /// A retry whose allocations arrive in another order is the same submission, because the order
    /// they were typed in is not part of what is deducted. This is the criterion's
    /// order-independence.
    /// </summary>
    [Fact]
    public void The_same_allocations_in_any_order_render_identically()
    {
        var asMachineRoute = Compute(
            "Friday run", (MachineA, 100, 6), (MachineA, 101, 1), (MachineB, 100, 4));
        var asProductRoute = Compute(
            "Friday run", (MachineB, 100, 4), (MachineA, 101, 1), (MachineA, 100, 6));

        Assert.Equal(asMachineRoute, asProductRoute);
    }

    [Fact]
    public void Moving_a_quantity_to_another_machine_is_a_different_submission()
    {
        Assert.NotEqual(
            Compute(null, (MachineA, 100, 6), (MachineB, 100, 4)),
            Compute(null, (MachineA, 100, 4), (MachineB, 100, 6)));
    }

    [Fact]
    public void A_different_machine_is_a_different_submission()
    {
        Assert.NotEqual(Compute(null, (MachineA, 100, 2)), Compute(null, (MachineB, 100, 2)));
    }

    [Fact]
    public void A_different_product_is_a_different_submission()
    {
        Assert.NotEqual(Compute(null, (MachineA, 100, 2)), Compute(null, (MachineA, 101, 2)));
    }

    [Fact]
    public void A_different_quantity_is_a_different_submission()
    {
        Assert.NotEqual(Compute(null, (MachineA, 100, 2)), Compute(null, (MachineA, 100, 3)));
    }

    [Fact]
    public void A_different_note_is_a_different_submission()
    {
        Assert.NotEqual(Compute("Friday run", (MachineA, 100, 2)), Compute("Saturday run", (MachineA, 100, 2)));
    }

    [Fact]
    public void No_note_and_a_note_are_different_submissions()
    {
        Assert.NotEqual(Compute(null, (MachineA, 100, 2)), Compute("no-notes", (MachineA, 100, 2)));
    }

    /// <summary>
    /// A note is rendered with its length, so it cannot borrow a field separator and make two
    /// different requests render the same way.
    /// </summary>
    [Fact]
    public void A_note_cannot_impersonate_the_rendering()
    {
        Assert.NotEqual(Compute("a|1", (MachineA, 100, 2)), Compute("a", (MachineA, 100, 2)));
    }

    [Fact]
    public void An_extra_allocation_is_a_different_submission()
    {
        Assert.NotEqual(
            Compute(null, (MachineA, 100, 2)),
            Compute(null, (MachineA, 100, 2), (MachineB, 100, 2)));
    }

    /// <summary>
    /// The two refill contracts share one per-business key namespace, so a key spent on one must
    /// never replay as the other. The version tag at the head of each rendering is what guarantees it:
    /// the comparable single-machine request - one machine, one product, one quantity, the same note -
    /// renders to a different fingerprint, so the use case refuses the key as reused rather than
    /// answering with a refill it did not record.
    /// </summary>
    [Fact]
    public void A_comparable_single_machine_request_renders_differently()
    {
        var batch = Compute("Friday run", (MachineA, 100, 2));
        var single = PickListRefillFingerprint.Compute(
            MachineA, "Friday run", [new PickListRefillRequestRow(100, 2)]);

        Assert.NotEqual(single, batch);
    }
}
