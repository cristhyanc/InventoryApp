using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Application.ReceiptScanning;

/// <summary>
/// The draft one validated reader answer produces (issue #1163): the arithmetic carried through, the
/// charges summed, the levy placed, and every suggestion that is not this business's own dropped.
///
/// <para>Every receipt here is invented (issue #1163's known constraints).</para>
/// </summary>
public class ReceiptScanDraftBuilderTests
{
    private static readonly IReadOnlySet<long> OwnProducts = new HashSet<long> { 91, 92, 93 };
    private static readonly IReadOnlySet<int> OwnSuppliers = new HashSet<int> { 7, 8 };

    /// <summary>
    /// The issue's first criterion end to end: a line of 2 packs x 24 at $30.00 becomes 48 units at
    /// $0.625, with the units and the unit cost marked as this application's suggestions and the
    /// printed figures marked as read.
    /// </summary>
    [Fact]
    public void A_pack_line_becomes_units_and_a_unit_cost()
    {
        var line = Assert.Single(Build(Payload(Line(packs: 2, unitsPerPack: 24, lineTotal: 30.00m)))!.Lines);

        Assert.Equal(48, line.Units!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.Units.Provenance);
        Assert.Equal(0.625m, line.UnitCost!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.UnitCost.Provenance);
        Assert.Equal(2, line.Packs!.Value);
        Assert.Equal(ReceiptValueProvenance.Read, line.Packs.Provenance);
        Assert.Equal(30.00m, line.LineTotal!.Value);
        Assert.Equal(ReceiptValueProvenance.Read, line.LineTotal.Provenance);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// The issue's foreign-id criterion: an id from another business, and one that names nothing, are
    /// both dropped, and the line keeps its printed text so the operator can still match it.
    /// </summary>
    [Theory]
    [InlineData(91, true)]
    [InlineData(4242, false)]
    [InlineData(1, false)]
    public void A_product_id_that_is_not_this_businesss_own_is_dropped(long productId, bool kept)
    {
        var line = Assert.Single(Build(Payload(Line(productId: productId)))!.Lines);

        Assert.Equal(kept ? productId : null, line.ProductId?.Value);
        Assert.Equal("COLA 375ML X 24", line.Text.Value);
    }

    /// <summary>A supplier id that is not this business's own is dropped the same way.</summary>
    [Theory]
    [InlineData(7, true)]
    [InlineData(4242, false)]
    public void A_supplier_id_that_is_not_this_businesss_own_is_dropped(long supplierId, bool kept)
    {
        var draft = Build(Payload(Line()) with { SupplierId = supplierId })!;

        Assert.Equal(kept ? (int?)supplierId : null, draft.SupplierId?.Value);
        Assert.Equal("Metro Beverages", draft.SupplierText!.Value);
    }

    /// <summary>
    /// A matched product is a suggestion and the supplier text is a quotation, which is the whole
    /// point of marking every value: one is this application's guess and the other is the receipt's
    /// own words.
    /// </summary>
    [Fact]
    public void A_matched_suggestion_is_marked_suggested_and_printed_text_is_marked_read()
    {
        var draft = Build(Payload(Line(productId: 91)) with { SupplierId = 7 })!;

        Assert.Equal(ReceiptValueProvenance.Suggested, draft.SupplierId!.Provenance);
        Assert.Equal(ReceiptValueProvenance.Read, draft.SupplierText!.Provenance);
        Assert.Equal(ReceiptValueProvenance.Suggested, Assert.Single(draft.Lines).ProductId!.Provenance);
    }

    /// <summary>
    /// The issue's non-stock criteria: bag and delivery lines become the draft's package and delivery
    /// costs and are not returned as lines, and an office item is returned as <c>other</c> with no
    /// product suggestion.
    /// </summary>
    [Fact]
    public void Delivery_and_package_lines_become_charges_and_a_non_stock_line_stays_a_line()
    {
        var draft = Build(Payload(
            Line(),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 9.50m, packs: 1, unitsPerPack: 1),
            Line(text: "CARRY BAGS", kind: ReceiptLineKind.Package, lineTotal: 0.50m, packs: 1, unitsPerPack: 1),
            Line(text: "A4 PAPER", kind: ReceiptLineKind.Other, lineTotal: 12.50m, packs: 1, unitsPerPack: 1, productId: 91)))!;

        Assert.Equal(9.50m, draft.DeliveryCost!.Value);
        Assert.Equal(0.50m, draft.PackageCost!.Value);

        var kinds = draft.Lines.Select(line => line.Kind).ToList();
        Assert.Equal([ReceiptLineKind.Stock, ReceiptLineKind.Other], kinds);

        // An "other" line is never a product of the business, however confidently the reader matched
        // it: it is not stock for resale.
        var other = draft.Lines.Single(line => line.Kind is ReceiptLineKind.Other);
        Assert.Null(other.ProductId);
        Assert.Equal("A4 PAPER", other.Text.Value);
    }

    /// <summary>Several delivery lines are one delivery cost, summed.</summary>
    [Fact]
    public void Several_charge_lines_are_summed_into_one_cost()
    {
        var draft = Build(Payload(
            Line(),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 9.50m, packs: 1, unitsPerPack: 1),
            Line(text: "FUEL LEVY", kind: ReceiptLineKind.Delivery, lineTotal: 2.25m, packs: 1, unitsPerPack: 1)))!;

        Assert.Equal(11.75m, draft.DeliveryCost!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, draft.DeliveryCost.Provenance);
    }

    /// <summary>
    /// A charge whose GST basis the receipt did not state is left for the operator rather than
    /// recorded possibly 10% wrong, and the whole charge is withheld rather than part of it.
    /// </summary>
    [Fact]
    public void A_charge_that_cannot_be_converted_leaves_no_charge_at_all()
    {
        var draft = Build(Payload(
            Line(),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 9.50m, packs: 1, unitsPerPack: 1),
            Line(
                text: "HANDLING",
                kind: ReceiptLineKind.Delivery,
                lineTotal: 2.25m,
                packs: 1,
                unitsPerPack: 1,
                basis: ReceiptPriceGstBasis.Unknown)))!;

        Assert.Null(draft.DeliveryCost);
    }

    /// <summary>
    /// The issue's delivered-quantity criteria: a line ordered 12 and delivered 11 uses 11, and a
    /// line with nothing delivered is left out altogether.
    /// </summary>
    [Fact]
    public void The_delivered_quantity_is_used_and_an_undelivered_line_is_left_out()
    {
        var draft = Build(Payload(
            Line(packs: 11, unitsPerPack: 1, lineTotal: 22.00m) with { OrderedPacks = 12 },
            Line(text: "CHIPS", packs: 0, unitsPerPack: 1, lineTotal: 0m) with { OrderedPacks = 6 }))!;

        var line = Assert.Single(draft.Lines);
        Assert.Equal(11, line.Units!.Value);
        Assert.Equal(11, line.Packs!.Value);
        Assert.Equal("COLA 375ML X 24", line.Text.Value);
    }

    /// <summary>
    /// The issue's discount criterion end to end: a discount of 1.50 under an item of 28.99 leaves
    /// that item at 27.49, and the amount is then a calculation rather than a quotation.
    /// </summary>
    [Fact]
    public void An_attached_discount_lowers_the_line_and_makes_the_amount_a_suggestion()
    {
        var line = Assert.Single(Build(Payload(
            Line(packs: 1, unitsPerPack: 1, lineTotal: 28.99m) with { DiscountTotal = 1.50m }))!.Lines);

        Assert.Equal(27.49m, line.LineTotal!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.LineTotal.Provenance);
        Assert.Equal(27.49m, line.UnitCost!.Value);
    }

    /// <summary>
    /// The issue's GST criteria: an ex-GST taxable line of $26.33 for a carton of 24 is $28.96, and
    /// its amount is a calculation. A line read from an inc-GST column has nothing added.
    /// </summary>
    [Fact]
    public void An_ex_gst_taxable_line_is_converted_onto_the_gst_inclusive_convention()
    {
        var line = Assert.Single(Build(Payload(Line(
            packs: 1,
            unitsPerPack: 24,
            lineTotal: 26.33m,
            basis: ReceiptPriceGstBasis.ExcludesGst)))!.Lines);

        Assert.Equal(28.96m, line.LineTotal!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.LineTotal.Provenance);
        Assert.Equal(28.96m / 24m, line.UnitCost!.Value);
    }

    /// <summary>
    /// The issue's unknown-basis criterion: the unit cost is left null and the line says which field
    /// it is short of. The GST hint carries what the receipt said, marker and all.
    /// </summary>
    [Fact]
    public void An_unknown_gst_basis_leaves_the_unit_cost_absent_and_is_listed_in_missing()
    {
        var line = Assert.Single(Build(Payload(Line(
            packs: 1,
            unitsPerPack: 24,
            lineTotal: 26.33m,
            basis: ReceiptPriceGstBasis.Unknown)))!.Lines);

        Assert.Null(line.UnitCost);
        Assert.Null(line.LineTotal);
        Assert.Contains(ReceiptLineMissingValue.PriceGstBasis, line.Missing);
        Assert.Contains(ReceiptLineMissingValue.UnitCost, line.Missing);
        Assert.Equal(ReceiptPriceGstBasis.Unknown, line.Gst.PriceBasis);
        Assert.Equal("*", line.Gst.Marker);
    }

    /// <summary>
    /// A supermarket tax invoice prints no basis beside its line prices but says its total includes
    /// GST. When the lines, the bag charge and the delivery fee add up to that total exactly, every
    /// line is priced as GST-inclusive: amounts, unit costs and charges all come back, and nothing is
    /// listed as missing for the GST basis.
    /// </summary>
    [Fact]
    public void Unknown_lines_that_add_up_to_a_gst_inclusive_total_are_priced_as_inc_gst()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 30, lineTotal: 32.00m, basis: ReceiptPriceGstBasis.Unknown),
            Line(text: "CHEESE AND CRACKERS 38G", packs: 4, unitsPerPack: 1, lineTotal: 10.00m, basis: ReceiptPriceGstBasis.Unknown),
            Line(text: "PAPER BAGS", kind: ReceiptLineKind.Package, packs: null, unitsPerPack: null, lineTotal: 2.00m, basis: ReceiptPriceGstBasis.Unknown),
            Line(text: "DELIVERY FEE", kind: ReceiptLineKind.Delivery, packs: null, unitsPerPack: null, lineTotal: 2.00m, basis: ReceiptPriceGstBasis.Unknown))
            with { ReceiptTotal = 46.00m })!;

        Assert.Equal(2, draft.Lines.Count);
        var cans = draft.Lines[0];
        Assert.Equal(32.00m, cans.LineTotal!.Value);
        Assert.Equal(32.00m / 30m, cans.UnitCost!.Value);
        Assert.DoesNotContain(ReceiptLineMissingValue.PriceGstBasis, cans.Missing);
        Assert.Equal(ReceiptPriceGstBasis.IncludesGst, cans.Gst.PriceBasis);
        Assert.Equal(2.50m, draft.Lines[1].UnitCost!.Value);
        Assert.Equal(2.00m, draft.DeliveryCost!.Value);
        Assert.Equal(2.00m, draft.PackageCost!.Value);
    }

    /// <summary>
    /// The same receipt with a total the lines do not add up to - here a fee the reader did not
    /// report - is left exactly as read: no amounts, and the GST basis listed as missing.
    /// </summary>
    [Fact]
    public void Unknown_lines_that_do_not_add_up_to_the_total_stay_unpriced()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 30, lineTotal: 32.00m, basis: ReceiptPriceGstBasis.Unknown),
            Line(text: "PAPER BAGS", kind: ReceiptLineKind.Package, packs: null, unitsPerPack: null, lineTotal: 2.00m, basis: ReceiptPriceGstBasis.Unknown))
            with { ReceiptTotal = 36.00m })!;

        var line = Assert.Single(draft.Lines);
        Assert.Null(line.LineTotal);
        Assert.Null(line.UnitCost);
        Assert.Contains(ReceiptLineMissingValue.PriceGstBasis, line.Missing);
        Assert.Equal(ReceiptPriceGstBasis.Unknown, line.Gst.PriceBasis);
        Assert.Null(draft.PackageCost);
    }

    /// <summary>
    /// The issue's missing-pack criterion: with no printed pack size the units and unit cost are
    /// absent and both are named, and the units per pack is null rather than one.
    /// </summary>
    [Fact]
    public void A_line_with_no_printed_pack_size_has_no_units_and_says_so()
    {
        var line = Assert.Single(Build(Payload(Line(packs: 2, unitsPerPack: null)))!.Lines);

        Assert.Null(line.Units);
        Assert.Null(line.UnitsPerPack);
        Assert.Null(line.UnitCost);
        Assert.Equal(
            [ReceiptLineMissingValue.UnitsPerPack, ReceiptLineMissingValue.UnitCost],
            line.Missing);
    }

    /// <summary>
    /// The issue's purchase-date criterion: the delivery date is the purchase date and is marked as
    /// read, because it is printed on the receipt.
    /// </summary>
    [Fact]
    public void The_purchase_date_is_the_delivery_date_and_is_marked_read()
    {
        var draft = Build(Payload(Line()))!;

        Assert.Equal(new DateOnly(2026, 10, 27), draft.PurchaseDate!.Value);
        Assert.Equal(ReceiptValueProvenance.Read, draft.PurchaseDate.Provenance);
    }

    /// <summary>An answer with no usable line is no draft, which the caller reports as unreadable.</summary>
    [Fact]
    public void An_answer_with_no_usable_line_is_no_draft()
    {
        Assert.Null(Build(Payload()));
        Assert.Null(Build(Payload(Line(packs: 0, unitsPerPack: 1, lineTotal: 0m))));
    }

    #region A pack size suggested from the last purchase (issue #1165)

    /// <summary>
    /// Issue #1165's first criterion: a carton the receipt did not print a pack size for takes the
    /// pack size the business last paid for, marked as this application's suggestion and carrying the
    /// purchase it came from - and the units and unit cost are filled in from it.
    /// </summary>
    [Fact]
    public void A_line_with_no_printed_pack_size_takes_the_last_purchases_pack_size()
    {
        var line = Assert.Single(Build(
            Payload(Line(
                text: "45G CC'S TASTY CHEESE",
                packs: 1,
                unitsPerPack: null,
                lineTotal: 28.96m,
                productId: 91))
                with
            { SupplierId = 7, ReceiptTotal = 28.96m },
            LastPurchase(91, 1.2067m))!.Lines);

        Assert.Equal(24, line.UnitsPerPack!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.UnitsPerPack.Provenance);
        Assert.Equal(24, line.Units!.Value);
        Assert.Equal(28.96m / 24, line.UnitCost!.Value);
        Assert.Empty(line.Missing);

        var suggestion = line.UnitsPerPackSuggestion!;
        Assert.Equal(24, suggestion.UnitsPerPack);
        Assert.Equal(ReceiptPackSizeSource.LastPurchase, suggestion.SuggestedFrom);
        Assert.Equal(PurchaseDate, suggestion.PurchaseDate);
    }

    /// <summary>
    /// Issue #1165's second criterion: two cartons for $57.92 are a pack size of 24 and 48 units -
    /// never 96. The packs delivered are divided out of the ratio and multiplied back into the units
    /// exactly once.
    /// </summary>
    [Fact]
    public void Two_cartons_on_one_line_are_forty_eight_units_and_never_ninety_six()
    {
        var line = Assert.Single(Build(
            Payload(Line(packs: 2, unitsPerPack: null, lineTotal: 57.92m, productId: 91))
                with
            { SupplierId = 7, ReceiptTotal = 57.92m },
            LastPurchase(91, 1.2067m))!.Lines);

        Assert.Equal(24, line.UnitsPerPack!.Value);
        Assert.Equal(48, line.Units!.Value);
        Assert.Equal(57.92m / 48, line.UnitCost!.Value);
    }

    /// <summary>
    /// Issue #1165's unknown-packs criterion: with no readable pack count nothing is suggested, and
    /// the line stays missing its pack size - rather than being given the units of the whole line as
    /// if they were the units of one pack.
    /// </summary>
    [Fact]
    public void A_line_whose_pack_count_is_unknown_gets_no_suggestion()
    {
        var line = Assert.Single(Build(
            Payload(Line(packs: null, unitsPerPack: null, lineTotal: 28.96m, productId: 91))
                with
            { SupplierId = 7 },
            LastPurchase(91, 1.2067m))!.Lines);

        Assert.Null(line.UnitsPerPack);
        Assert.Null(line.UnitsPerPackSuggestion);
        Assert.Null(line.Units);
        Assert.Contains(ReceiptLineMissingValue.Packs, line.Missing);
        Assert.Contains(ReceiptLineMissingValue.UnitsPerPack, line.Missing);
    }

    /// <summary>
    /// Issue #1165's tolerance criterion through the draft: a carton whose price has moved produces a
    /// ratio of 24.7 and is left missing, while the same carton at essentially the same price
    /// produces 23.9 and is 24.
    /// </summary>
    [Theory]
    [InlineData(29.81, null)]
    [InlineData(28.84, 24)]
    public void A_ratio_outside_the_tolerance_leaves_the_line_missing(double lineTotal, int? expected)
    {
        var line = Assert.Single(Build(
            Payload(Line(packs: 1, unitsPerPack: null, lineTotal: (decimal)lineTotal, productId: 91))
                with
            { SupplierId = 7, ReceiptTotal = (decimal)lineTotal },
            LastPurchase(91, 1.2067m))!.Lines);

        Assert.Equal(expected, line.UnitsPerPack?.Value);
        Assert.Equal(expected is null, line.UnitsPerPackSuggestion is null);
        Assert.Equal(expected is null, line.Units is null);
    }

    /// <summary>
    /// Issue #1165's "never overwrite" criterion: a pack size the receipt printed is used and no
    /// lookup changes it, even when the history would have said something else. The value stays a
    /// quotation and carries no suggestion marker.
    /// </summary>
    [Fact]
    public void A_pack_size_printed_on_the_receipt_is_never_replaced()
    {
        var line = Assert.Single(Build(
            Payload(Line(packs: 1, unitsPerPack: 24, lineTotal: 28.96m, productId: 91))
                with
            { SupplierId = 7, ReceiptTotal = 28.96m },
            // Would have suggested 12 had it been asked.
            LastPurchase(91, 2.4133m))!.Lines);

        Assert.Equal(24, line.UnitsPerPack!.Value);
        Assert.Equal(ReceiptValueProvenance.Read, line.UnitsPerPack.Provenance);
        Assert.Null(line.UnitsPerPackSuggestion);
        Assert.Equal(24, line.Units!.Value);
    }

    /// <summary>
    /// Issue #1165's no-history criterion: a product this business has never bought from this
    /// supplier gets no suggestion, and neither does a line whose suggested product is not this
    /// business's own - the history is keyed by product, and a foreign id names none of it.
    /// </summary>
    [Theory]
    [InlineData(91, 92)]
    [InlineData(4242, 4242)]
    public void A_product_with_no_history_of_its_own_gets_no_suggestion(long lineProduct, long withHistory)
    {
        var line = Assert.Single(Build(
            Payload(Line(packs: 1, unitsPerPack: null, lineTotal: 28.96m, productId: lineProduct))
                with
            { SupplierId = 7 },
            LastPurchase(withHistory, 1.2067m))!.Lines);

        Assert.Null(line.UnitsPerPack);
        Assert.Null(line.UnitsPerPackSuggestion);
    }

    /// <summary>
    /// Only a stock line is ever given one. An <c>other</c> line is not stock for resale, carries no
    /// product suggestion at all, and is not quietly given a pack size either.
    /// </summary>
    [Fact]
    public void A_non_stock_line_is_never_given_a_pack_size()
    {
        var line = Assert.Single(Build(
            Payload(Line(
                text: "A4 PAPER",
                kind: ReceiptLineKind.Other,
                packs: 1,
                unitsPerPack: null,
                lineTotal: 28.96m,
                productId: 91))
                with
            { SupplierId = 7 },
            LastPurchase(91, 1.2067m))!.Lines);

        Assert.Null(line.UnitsPerPack);
        Assert.Null(line.UnitsPerPackSuggestion);
        Assert.Null(line.ProductId);
    }

    /// <summary>
    /// The ratio is judged against the amount the receipt printed, never the amount after a levy
    /// share - the levy is shared by units, so judging it against a levied amount would make the
    /// suggestion depend on the pack size being suggested. This receipt's $2.40 levy would turn
    /// 28.96 into 31.36 and a ratio of 24 into 26, which is itself inside the tolerance: the pack
    /// size is 24, and the levy then has a unit count to be shared by.
    /// </summary>
    [Fact]
    public void The_ratio_is_judged_before_a_levy_share_is_added()
    {
        var draft = Build(
            Payload(
                Line(packs: 1, unitsPerPack: null, lineTotal: 28.96m, productId: 91, isDrinkContainer: true),
                Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 2.40m, packs: 1, unitsPerPack: 1))
                with
            { SupplierId = 7, ReceiptTotal = 31.36m },
            LastPurchase(91, 1.2067m))!;

        var line = Assert.Single(draft.Lines);
        Assert.Equal(24, line.UnitsPerPack!.Value);
        Assert.Equal(24, line.Units!.Value);
        Assert.Equal(31.36m, line.LineTotal!.Value);
        Assert.Equal(31.36m / 24, line.UnitCost!.Value);
    }

    #endregion

    #region The levy

    /// <summary>
    /// The issue's "not included" criterion end to end: a $1.10 levy the line prices do not carry is
    /// added to the one drink line's amount, and never to the delivery cost.
    /// </summary>
    [Fact]
    public void A_levy_the_prices_do_not_carry_is_added_to_the_drink_line()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = 56.10m })!;

        var line = Assert.Single(draft.Lines);
        Assert.Equal(ReceiptLineKind.Stock, line.Kind);
        Assert.Equal(46.10m, line.LineTotal!.Value);
        Assert.Equal(4.61m, line.UnitCost!.Value);
        Assert.Equal(10.00m, draft.DeliveryCost!.Value);
    }

    /// <summary>
    /// The issue's "already included" criterion: a levy the line prices already carry is not added
    /// again, and the levy line is not returned either - it is not a cost of its own.
    /// </summary>
    [Fact]
    public void A_levy_the_prices_already_carry_is_not_added_again()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = 55.00m })!;

        var line = Assert.Single(draft.Lines);
        Assert.Equal(45.00m, line.LineTotal!.Value);
        Assert.Equal(4.50m, line.UnitCost!.Value);
        Assert.DoesNotContain(ReceiptLineKind.Levy, draft.Lines.Select(entry => entry.Kind));
    }

    /// <summary>
    /// The issue's criterion about an <c>other</c> line: the office item's amount counts towards the
    /// reconciliation, so the levy is found to be already included - and the office item is still
    /// returned as <c>other</c>, never as a stock line and never with a product suggestion.
    /// </summary>
    [Fact]
    public void A_non_stock_line_counts_towards_the_reconciliation_and_is_still_returned_as_other()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true),
            Line(text: "A4 PAPER", kind: ReceiptLineKind.Other, lineTotal: 12.50m, packs: 1, unitsPerPack: 1, productId: 91),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = 67.50m })!;

        var drink = draft.Lines.Single(line => line.Kind is ReceiptLineKind.Stock);
        Assert.Equal(45.00m, drink.LineTotal!.Value);

        var office = draft.Lines.Single(line => line.Kind is ReceiptLineKind.Other);
        Assert.Null(office.ProductId);
        Assert.Equal(12.50m, office.LineTotal!.Value);
        Assert.DoesNotContain(ReceiptLineKind.Levy, draft.Lines.Select(line => line.Kind));
    }

    /// <summary>
    /// The issue's "unresolved" criterion: with totals that match neither reading, no line's cost
    /// changes and the levy comes back as its own flagged line with its printed amount - never as a
    /// stock line, and never with a product suggestion.
    /// </summary>
    [Fact]
    public void A_levy_the_totals_cannot_place_is_returned_as_its_own_flagged_line()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1, productId: 91))
            with
        { ReceiptTotal = 60.00m })!;

        var drink = draft.Lines.Single(line => line.Kind is ReceiptLineKind.Stock);
        Assert.Equal(45.00m, drink.LineTotal!.Value);
        Assert.False(drink.LevyUnresolved);

        var levy = draft.Lines.Single(line => line.Kind is ReceiptLineKind.Levy);
        Assert.True(levy.LevyUnresolved);
        Assert.Equal(1.10m, levy.LineTotal!.Value);
        Assert.Null(levy.ProductId);
        Assert.Null(levy.Units);
        Assert.Equal("CONTAINER DEPOSIT", levy.Text.Value);
    }

    /// <summary>
    /// And the issue's last levy criterion: with no line marked as a drink, the levy stays a separate
    /// flagged line even when the totals say it is an extra cost.
    /// </summary>
    [Fact]
    public void A_levy_with_no_drink_line_is_returned_as_its_own_flagged_line()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: false),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = 56.10m })!;

        Assert.Equal(45.00m, draft.Lines.Single(line => line.Kind is ReceiptLineKind.Stock).LineTotal!.Value);
        Assert.True(draft.Lines.Single(line => line.Kind is ReceiptLineKind.Levy).LevyUnresolved);
    }

    /// <summary>
    /// The reconciliation is made on one basis, and the printed total's basis is that basis: a
    /// receipt whose lines and total are both ex GST reconciles without any conversion rounding, so
    /// an already-included levy is still found to be included.
    /// </summary>
    [Fact]
    public void The_reconciliation_is_made_on_the_printed_totals_own_basis()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true, basis: ReceiptPriceGstBasis.ExcludesGst),
            Line(text: "FREIGHT", kind: ReceiptLineKind.Delivery, lineTotal: 10.00m, packs: 1, unitsPerPack: 1, basis: ReceiptPriceGstBasis.ExcludesGst),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1, basis: ReceiptPriceGstBasis.ExcludesGst))
            with
        { ReceiptTotal = 55.00m, ReceiptTotalBasis = ReceiptPriceGstBasis.ExcludesGst })!;

        // Not added again, and the line is still published GST-inclusive, which is the application's
        // own convention: 45.00 ex GST is 49.50.
        var line = Assert.Single(draft.Lines);
        Assert.Equal(49.50m, line.LineTotal!.Value);
        Assert.DoesNotContain(ReceiptLineKind.Levy, draft.Lines.Select(entry => entry.Kind));
    }

    /// <summary>
    /// A receipt with no printed total cannot be reconciled, so the levy stays a flagged line: there
    /// is nothing to compare the lines against.
    /// </summary>
    [Fact]
    public void A_levy_on_a_receipt_with_no_printed_total_is_left_unresolved()
    {
        var draft = Build(Payload(
            Line(packs: 1, unitsPerPack: 10, lineTotal: 45.00m, isDrinkContainer: true),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 1.10m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = null })!;

        Assert.True(draft.Lines.Single(line => line.Kind is ReceiptLineKind.Levy).LevyUnresolved);
    }

    /// <summary>
    /// A levy shared across two drink lines is shared by units, and the two shares sum to exactly
    /// the printed levy.
    /// </summary>
    [Fact]
    public void A_levy_is_shared_across_drink_lines_by_units()
    {
        var draft = Build(Payload(
            Line(text: "COLA 375ML X 10", packs: 1, unitsPerPack: 10, lineTotal: 20.00m, isDrinkContainer: true),
            Line(text: "WATER 600ML X 30", packs: 1, unitsPerPack: 30, lineTotal: 35.00m, isDrinkContainer: true),
            Line(text: "CONTAINER DEPOSIT", kind: ReceiptLineKind.Levy, lineTotal: 4.00m, packs: 1, unitsPerPack: 1))
            with
        { ReceiptTotal = 59.00m })!;

        var cola = draft.Lines.Single(line => line.Text.Value.StartsWith("COLA", StringComparison.Ordinal));
        var water = draft.Lines.Single(line => line.Text.Value.StartsWith("WATER", StringComparison.Ordinal));

        Assert.Equal(21.00m, cola.LineTotal!.Value);
        Assert.Equal(38.00m, water.LineTotal!.Value);
        Assert.Equal(59.00m, cola.LineTotal.Value + water.LineTotal.Value);
    }

    #endregion

    /// <summary>The date of the purchase every suggestion in this file is read from.</summary>
    private static readonly DateOnly PurchaseDate = new(2026, 9, 14);

    /// <summary>
    /// A history holding one product's last purchase from the suggested supplier, at
    /// <paramref name="unitCost"/> a unit.
    /// </summary>
    private static ReceiptPackSizeHistory LastPurchase(long productId, decimal unitCost) =>
        ReceiptPackSizeHistory.Of([new ReceiptScanLastPurchase(productId, unitCost, PurchaseDate)]);

    private static ReceiptScanDraft? Build(
        ReceiptScanPayload payload,
        ReceiptPackSizeHistory? history = null) =>
        ReceiptScanDraftBuilder.Build(
            payload, OwnProducts, OwnSuppliers, history ?? ReceiptPackSizeHistory.None);

    private static ReceiptScanPayload Payload(params ReceiptScanPayloadLine[] lines) =>
        new(
            SupplierName: "Metro Beverages",
            SupplierId: null,
            OrderDate: new DateOnly(2026, 10, 25),
            ReceiptDate: new DateOnly(2026, 10, 26),
            DeliveryDate: new DateOnly(2026, 10, 27),
            InvoiceNumber: "INV-4471",
            ReceiptTotal: 30.00m,
            ReceiptTotalBasis: ReceiptPriceGstBasis.IncludesGst,
            GstTotal: null,
            Lines: lines);

    private static ReceiptScanPayloadLine Line(
        string text = "COLA 375ML X 24",
        ReceiptLineKind kind = ReceiptLineKind.Stock,
        int? packs = 2,
        int? unitsPerPack = 24,
        decimal? lineTotal = 30.00m,
        ReceiptPriceGstBasis basis = ReceiptPriceGstBasis.IncludesGst,
        long? productId = null,
        bool isDrinkContainer = false) =>
        new(
            text,
            kind,
            packs,
            OrderedPacks: null,
            unitsPerPack,
            unitsPerPack is null ? ReceiptPackSize.NotPrinted : ReceiptPackSize.Printed,
            lineTotal,
            DiscountTotal: 0m,
            basis,
            ReceiptLineTaxability.Taxable,
            GstMarker: "*",
            productId,
            isDrinkContainer);
}
