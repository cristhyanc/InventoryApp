using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The sign every manual adjustment reason is allowed to move stock in (issue #1242), and the
/// Restock cost rules that came with the policy from the former
/// <c>InventoryApi.Services.StockService.Adjust</c> (issue #282).
///
/// <para><see cref="FullMatrix"/> is the complete reason × sign matrix - positive, zero and negative
/// for every reason the vocabulary declares - so a reason added or re-signed later cannot be left
/// with no stated direction. The write-off reasons are the ones the issue exists for: a "Damaged 5"
/// typed the natural way used to <em>add</em> 5 units to storage and record no loss, and a
/// "Restock -4" used to lower physical stock while the cost replay gave it no costing effect at
/// all.</para>
/// </summary>
public class ManualStockAdjustmentPolicyTests
{
    /// <summary>
    /// A unit cost for the combinations that need one, so a Restock refusal in the matrix is about
    /// the quantity's sign and never about a missing cost.
    /// </summary>
    private const decimal UnitCost = 1.25m;

    /// <summary>
    /// The machine a refill names, so a MachineRefill combination in the matrix is about the sign
    /// and never about the machine issue #1331 now requires. Every other reason names none.
    /// </summary>
    private const long MachineId = 5;

    /// <summary>
    /// Every reason against a positive, a zero and a negative quantity change, with the message the
    /// policy refuses it with or <c>null</c> when it is accepted.
    /// </summary>
    public static TheoryData<StockAdjustmentReason, int, string?> FullMatrix()
    {
        var data = new TheoryData<StockAdjustmentReason, int, string?>();

        foreach (var reason in new[]
                 {
                     StockAdjustmentReason.Damaged, StockAdjustmentReason.Expired, StockAdjustmentReason.Sale,
                 })
        {
            var message = ManualStockAdjustmentPolicy.RemovesStockMessage(reason);
            data.Add(reason, 5, message);
            data.Add(reason, 0, message);
            data.Add(reason, -5, null);
        }

        data.Add(StockAdjustmentReason.Correction, 5, ManualStockAdjustmentPolicy.CorrectionRemovesStockMessage);
        data.Add(StockAdjustmentReason.Correction, 0, ManualStockAdjustmentPolicy.CorrectionRemovesStockMessage);
        data.Add(StockAdjustmentReason.Correction, -5, null);

        data.Add(StockAdjustmentReason.Restock, 5, null);
        data.Add(StockAdjustmentReason.Restock, 0, ManualStockAdjustmentPolicy.RestockAddsStockMessage);
        data.Add(StockAdjustmentReason.Restock, -5, ManualStockAdjustmentPolicy.RestockAddsStockMessage);

        // An internal transfer between storage and a machine keeps both directions: a refill leaves
        // storage (negative) and a return to storage is the same movement positive, and neither
        // changes costing quantity or value.
        data.Add(StockAdjustmentReason.MachineRefill, 5, null);
        data.Add(StockAdjustmentReason.MachineRefill, 0, null);
        data.Add(StockAdjustmentReason.MachineRefill, -5, null);

        return data;
    }

    [Theory]
    [MemberData(nameof(FullMatrix))]
    public void Every_reason_moves_stock_in_one_direction(
        StockAdjustmentReason reason, int quantityChange, string? refusal)
    {
        var unitCost = reason == StockAdjustmentReason.Restock ? UnitCost : (decimal?)null;
        var machineId = reason == StockAdjustmentReason.MachineRefill ? MachineId : (long?)null;

        if (refusal is null)
        {
            ManualStockAdjustmentPolicy.Validate(reason, quantityChange, unitCost, machineId);
            return;
        }

        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(reason, quantityChange, unitCost, machineId));

        Assert.Equal(refusal, exception.Message);
    }

    /// <summary>
    /// The refusal has to be actionable: it names the reason it is about and the sign that reason
    /// expects, and the Restock one names the reasons that do remove stock.
    /// </summary>
    [Theory]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Expired)]
    [InlineData(StockAdjustmentReason.Sale)]
    public void A_write_off_refusal_names_its_reason_and_the_expected_sign(StockAdjustmentReason reason)
    {
        var message = ManualStockAdjustmentPolicy.RemovesStockMessage(reason);

        Assert.Contains(reason.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("negative", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restock_refusal_points_at_the_reasons_that_remove_stock()
    {
        Assert.Contains("Restock", ManualStockAdjustmentPolicy.RestockAddsStockMessage, StringComparison.Ordinal);
        Assert.Contains("positive", ManualStockAdjustmentPolicy.RestockAddsStockMessage, StringComparison.Ordinal);
        Assert.Contains(
            "Correction, Damaged or Expired",
            ManualStockAdjustmentPolicy.RestockAddsStockMessage,
            StringComparison.Ordinal);
    }

    /// <summary>The Correction message is the one the policy has always thrown, unchanged.</summary>
    [Fact]
    public void The_correction_refusal_is_unchanged()
    {
        Assert.Equal("Correction quantity must remove stock.", ManualStockAdjustmentPolicy.CorrectionRemovesStockMessage);
    }

    [Fact]
    public void Positive_restock_without_a_unit_cost_is_rejected()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 3, null, null));

        Assert.Equal("Unit cost is required for a positive Restock adjustment.", exception.Message);
    }

    [Fact]
    public void Positive_restock_with_a_negative_unit_cost_is_rejected()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 3, -1m, null));

        Assert.Equal("Unit cost cannot be negative for a positive Restock adjustment.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    public void Positive_restock_with_a_valid_unit_cost_is_accepted(decimal unitCost)
    {
        ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 4, unitCost, null);
    }

    /// <summary>
    /// A Restock that removes stock is refused for its sign, not for its cost: the quantity is the
    /// thing to correct, and a cost entered on it would not make it a write-off.
    /// </summary>
    [Fact]
    public void A_negative_restock_is_refused_whether_or_not_it_carries_a_cost()
    {
        foreach (var unitCost in new decimal?[] { null, 1.25m })
        {
            var exception = Assert.Throws<DomainValidationException>(
                () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, -4, unitCost, null));

            Assert.Equal(ManualStockAdjustmentPolicy.RestockAddsStockMessage, exception.Message);
        }
    }

    /// <summary>
    /// A manual Machine refill must name its machine, in both directions (issue #1331): without one
    /// the transfer moved storage stock and reached no machine's ledger.
    /// </summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(5)]
    [InlineData(0)]
    public void A_machine_refill_without_a_machine_is_refused(int quantityChange)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(
                StockAdjustmentReason.MachineRefill, quantityChange, null, null));

        Assert.Equal(ManualStockAdjustmentPolicy.MachineRequiredMessage, exception.Message);
    }

    /// <summary>The refusal has to say which field is missing, without naming a machine.</summary>
    [Fact]
    public void The_machine_refusal_asks_for_the_machine()
    {
        Assert.Contains("Machine refill", ManualStockAdjustmentPolicy.MachineRequiredMessage, StringComparison.Ordinal);
        Assert.Contains("machine", ManualStockAdjustmentPolicy.MachineRequiredMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other reason is unaffected (issue #1331): it names no machine today and naming none is
    /// still accepted. Only the refill's own half of the transfer made the machine necessary.
    /// </summary>
    [Theory]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Expired)]
    [InlineData(StockAdjustmentReason.Sale)]
    [InlineData(StockAdjustmentReason.Correction)]
    public void A_reason_other_than_a_refill_still_needs_no_machine(StockAdjustmentReason reason)
    {
        ManualStockAdjustmentPolicy.Validate(reason, -2, null, null);
        ManualStockAdjustmentPolicy.Validate(reason, -2, null, MachineId);
    }

    /// <summary>
    /// A cost on a reason that removes stock is still ignored rather than refused: the movement is
    /// costed from the product's own average, and no caller is told to strip a field it sent.
    /// </summary>
    [Theory]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Expired)]
    [InlineData(StockAdjustmentReason.Sale)]
    [InlineData(StockAdjustmentReason.Correction)]
    public void A_unit_cost_on_a_removal_reason_is_neither_required_nor_refused(StockAdjustmentReason reason)
    {
        ManualStockAdjustmentPolicy.Validate(reason, -2, null, null);
        ManualStockAdjustmentPolicy.Validate(reason, -2, 1.25m, null);
    }
}
