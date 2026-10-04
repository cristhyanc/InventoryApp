using Inventory.Domain.Costing;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Costing;

/// <summary>
/// The cost-ledger fingerprint (issue #359). It is what lets the costing-repair apply reject a
/// stale preview the way the inventory-cost transition rejects one: the preview reports the
/// fingerprint of the replay inputs it was computed from, and the apply recomputes it from its own
/// authoritative read and refuses to write when it differs. So the fingerprint has to be stable
/// across equal input in any order, and sensitive to every input the replay actually consumes.
/// </summary>
public class CostLedgerFingerprintTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The default ledger, with one input at a time replaced. <paramref name="withoutBaseline"/> is
    /// how "no baseline at all" is expressed, since a <c>null</c> argument means "use the default".
    /// </summary>
    private static string Compute(
        CostReplayProduct? product = null,
        IReadOnlyCollection<CostReplayAdjustment>? adjustments = null,
        IReadOnlyCollection<CostReplaySale>? sales = null,
        IReadOnlyCollection<CostReplayRepair>? repairs = null,
        CostReplayBaseline? baseline = null,
        bool withoutBaseline = false) =>
        CostLedgerFingerprint.Compute(
            product ?? new CostReplayProduct(7, 5, 4, 8m),
            adjustments ?? [Adjustment(1), Adjustment(2)],
            sales ?? [new CostReplaySale(100, T0.AddMinutes(1))],
            repairs ?? [new CostReplayRepair(1, T0.AddMinutes(2), 3, 1.5m)],
            withoutBaseline ? null : baseline ?? new CostReplayBaseline(T0, 1, 2, 3m));

    private static CostReplayAdjustment Adjustment(int id, decimal? unitCost = 2m) =>
        new(id, T0.AddMinutes(id), StockAdjustmentReason.Restock, 4, unitCost, true);

    [Fact]
    public void The_same_ledger_produces_the_same_fingerprint_whatever_order_it_is_read_in()
    {
        var ordered = Compute(adjustments: [Adjustment(1), Adjustment(2)]);
        var reversed = Compute(adjustments: [Adjustment(2), Adjustment(1)]);

        Assert.Equal(ordered, reversed);
        Assert.Equal(64, ordered.Length);
    }

    [Fact]
    public void An_equal_decimal_written_with_a_different_scale_is_the_same_ledger()
    {
        Assert.Equal(
            Compute(repairs: [new CostReplayRepair(1, T0, 3, 1.5m)]),
            Compute(repairs: [new CostReplayRepair(1, T0, 3, 1.500000m)]));
    }

    [Fact]
    public void An_empty_ledger_has_a_fingerprint_and_it_differs_from_a_populated_one()
    {
        var empty = CostLedgerFingerprint.Compute(new CostReplayProduct(7, 0, null, null), [], [], [], null);

        Assert.NotEmpty(empty);
        Assert.NotEqual(empty, Compute());
    }

    [Fact]
    public void Every_replay_input_changes_the_fingerprint()
    {
        var baseline = Compute();

        Assert.NotEqual(baseline, Compute(product: new CostReplayProduct(7, 6, 4, 8m)));
        Assert.NotEqual(baseline, Compute(product: new CostReplayProduct(7, 5, 5, 8m)));
        Assert.NotEqual(baseline, Compute(product: new CostReplayProduct(7, 5, 4, 9m)));
        Assert.NotEqual(baseline, Compute(product: new CostReplayProduct(7, 5, null, 8m)));
        Assert.NotEqual(baseline, Compute(adjustments: [Adjustment(1)]));
        Assert.NotEqual(baseline, Compute(adjustments: [Adjustment(1), Adjustment(2, unitCost: 3m)]));
        Assert.NotEqual(baseline, Compute(adjustments: [Adjustment(1), Adjustment(2, unitCost: null)]));
        Assert.NotEqual(baseline, Compute(adjustments:
        [
            Adjustment(1),
            new CostReplayAdjustment(2, T0.AddMinutes(2), StockAdjustmentReason.Correction, 4, 2m, true),
        ]));
        Assert.NotEqual(baseline, Compute(adjustments:
        [
            Adjustment(1),
            new CostReplayAdjustment(2, T0.AddMinutes(2), StockAdjustmentReason.Restock, 5, 2m, true),
        ]));
        Assert.NotEqual(baseline, Compute(adjustments:
        [
            Adjustment(1),
            new CostReplayAdjustment(2, T0.AddMinutes(2), StockAdjustmentReason.Restock, 4, 2m, false),
        ]));
        Assert.NotEqual(baseline, Compute(adjustments:
        [
            Adjustment(1),
            new CostReplayAdjustment(2, T0.AddMinutes(3), StockAdjustmentReason.Restock, 4, 2m, true),
        ]));
        Assert.NotEqual(baseline, Compute(sales: [new CostReplaySale(101, T0.AddMinutes(1))]));
        Assert.NotEqual(baseline, Compute(sales: [new CostReplaySale(100, T0.AddMinutes(9))]));
        Assert.NotEqual(baseline, Compute(sales: []));
        Assert.NotEqual(baseline, Compute(repairs: [new CostReplayRepair(2, T0.AddMinutes(2), 3, 1.5m)]));
        Assert.NotEqual(baseline, Compute(repairs: [new CostReplayRepair(1, T0.AddMinutes(3), 3, 1.5m)]));
        Assert.NotEqual(baseline, Compute(repairs: [new CostReplayRepair(1, T0.AddMinutes(2), 4, 1.5m)]));
        Assert.NotEqual(baseline, Compute(repairs: [new CostReplayRepair(1, T0.AddMinutes(2), 3, 1.6m)]));
        Assert.NotEqual(baseline, Compute(repairs: []));
        Assert.NotEqual(baseline, Compute(withoutBaseline: true));
        Assert.NotEqual(baseline, Compute(baseline: new CostReplayBaseline(T0.AddTicks(1), 1, 2, 3m)));
        Assert.NotEqual(baseline, Compute(baseline: new CostReplayBaseline(T0, 2, 2, 3m)));
        Assert.NotEqual(baseline, Compute(baseline: new CostReplayBaseline(T0, 1, 3, 3m)));
        Assert.NotEqual(baseline, Compute(baseline: new CostReplayBaseline(T0, 1, 2, 4m)));
    }

    /// <summary>
    /// Timestamps are fingerprinted as instants, not as formatted text: SQLite materialises a
    /// stored <c>DateTime</c> as <see cref="DateTimeKind.Unspecified"/> (see the AppDbContext
    /// comments), and the replay compares ticks alone, so the fingerprint must not make the same
    /// instant look like two different ledgers depending on which read produced it.
    /// </summary>
    [Fact]
    public void A_timestamps_kind_does_not_change_the_fingerprint()
    {
        var utc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.Equal(
            Compute(sales: [new CostReplaySale(100, utc)]),
            Compute(sales: [new CostReplaySale(100, unspecified)]));
    }
}
