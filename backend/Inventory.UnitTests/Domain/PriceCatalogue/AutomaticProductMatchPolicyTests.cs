using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Domain.PriceCatalogue;

/// <summary>
/// The automatic-match rule of issue #941 (parent #817), condition by condition: when a product
/// waiting for a match may be matched to an existing master product without anybody looking at it.
///
/// <para>Each condition is asserted twice - passing and failing on its own - because the rule is a
/// conjunction and a conjunction with one broken term still passes most of the time. The pairs the
/// issue names by hand are here as named cases: <c>Coke Zero 375 mL</c> against
/// <c>Coke Classic 375 mL</c>, <c>Pepsi Max</c> against <c>Pepsi</c>, and 375 ml against 0.375 l and
/// against 355 ml.</para>
/// </summary>
public class AutomaticProductMatchPolicyTests
{
    private static readonly ProductMatchCandidate ColaClassic =
        new(10, "Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre);

    #region The unit label's own size

    /// <summary>
    /// The two spellings the issue names, and the equality that makes them comparable: a label is a
    /// number and a unit, and the unit is converted away before anything is compared.
    /// </summary>
    [Theory]
    [InlineData("375mL", 375, ProductSizeDimension.Volume)]
    [InlineData("375 ML", 375, ProductSizeDimension.Volume)]
    [InlineData("375 millilitres", 375, ProductSizeDimension.Volume)]
    [InlineData("1.25L", 1250, ProductSizeDimension.Volume)]
    [InlineData("0.375 l", 375, ProductSizeDimension.Volume)]
    [InlineData("500g", 500, ProductSizeDimension.Weight)]
    [InlineData("1kg", 1000, ProductSizeDimension.Weight)]
    [InlineData("6 each", 6, ProductSizeDimension.Count)]
    public void A_unit_label_parses_to_a_canonical_size(
        string unitLabel, int canonicalValue, ProductSizeDimension dimension)
    {
        Assert.True(AutomaticProductMatchPolicy.TryParseSize(unitLabel, out var size));

        Assert.Equal(canonicalValue, size!.CanonicalValue);
        Assert.Equal(dimension, size.Dimension);
    }

    /// <summary>
    /// A label that does not say a size parses to nothing, which refuses an automatic match rather
    /// than guessing one. Every one of these is a label a real catalogue holds.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("can")]
    [InlineData("each")]
    [InlineData("375")]
    [InlineData("375ml bottle")]
    [InlineData("375 fluid ounces")]
    [InlineData("0ml")]
    [InlineData("1.2.5L")]
    public void A_label_that_states_no_size_parses_to_nothing(string? unitLabel)
    {
        Assert.False(AutomaticProductMatchPolicy.TryParseSize(unitLabel, out var size));
        Assert.Null(size);
    }

    /// <summary>
    /// 375 ml and 0.375 l are the same size; 375 ml and 355 ml are not, and neither are 375 ml and
    /// 375 g - a number with another dimension behind it is another size, not a near miss.
    /// </summary>
    [Theory]
    [InlineData("375mL", 375, MasterProductSizeUnit.Millilitre, true)]
    [InlineData("375mL", 0.375, MasterProductSizeUnit.Litre, true)]
    [InlineData("0.375L", 375, MasterProductSizeUnit.Millilitre, true)]
    [InlineData("375mL", 355, MasterProductSizeUnit.Millilitre, false)]
    [InlineData("375mL", 375, MasterProductSizeUnit.Gram, false)]
    [InlineData("1.25L", 1250, MasterProductSizeUnit.Millilitre, true)]
    [InlineData("1kg", 1000, MasterProductSizeUnit.Gram, true)]
    public void Sizes_agree_only_in_the_same_dimension_and_the_same_canonical_value(
        string unitLabel, double masterSize, MasterProductSizeUnit masterUnit, bool agree)
    {
        Assert.True(AutomaticProductMatchPolicy.TryParseSize(unitLabel, out var size));

        var candidate = new ProductMatchCandidate(
            1, "Coca-Cola", "Classic", (decimal)masterSize, masterUnit);

        Assert.Equal(agree, AutomaticProductMatchPolicy.SizesAgree(size!, candidate));
    }

    #endregion

    #region The brand and the variant

    /// <summary>
    /// The brand appears word for word, with case and punctuation folded - and a word that merely
    /// contains it does not count, which is what keeps "Cola Classic" from being Coca-Cola's.
    /// </summary>
    [Theory]
    [InlineData("Coca-Cola", "Coca-Cola Classic 375mL", true)]
    [InlineData("Coca-Cola", "coca cola classic", true)]
    [InlineData("Coca-Cola", "COCA-COLA  CLASSIC", true)]
    [InlineData("Coca-Cola", "Cola Classic", false)]
    [InlineData("Pepsi", "Coca-Cola Classic", false)]
    [InlineData("Coca-Cola", "Coke Classic", false)]
    public void A_brand_appears_in_a_search_name_word_for_word(
        string brand, string searchName, bool appears) =>
        Assert.Equal(appears, AutomaticProductMatchPolicy.BrandAppearsIn(brand, searchName));

    /// <summary>
    /// Every word of the master product's variant has to appear in the search name. The reverse is
    /// deliberately allowed - a product name may say more than the master product does - and what
    /// catches the words that matter is the descriptor rule below.
    /// </summary>
    [Theory]
    [InlineData("Classic", "Coca-Cola Classic 375mL", true)]
    [InlineData("Zero Sugar", "Coca-Cola Zero Sugar", true)]
    [InlineData("Zero Sugar", "Coca-Cola Zero", false)]
    [InlineData("Max", "Pepsi", false)]
    [InlineData("Classic", "Coca-Cola", false)]
    public void Every_variant_word_of_the_master_product_appears_in_the_search_name(
        string masterName, string searchName, bool agrees) =>
        Assert.Equal(agrees, AutomaticProductMatchPolicy.VariantAgrees(masterName, searchName));

    #endregion

    #region The conflicting descriptors

    /// <summary>
    /// The two pairs the issue names, and the rest of the list: a descriptor on one side and not the
    /// other is a conflict whichever side it is on.
    /// </summary>
    [Theory]
    [InlineData("Coke Zero 375mL", "Coke Classic", true)]
    [InlineData("Coke Classic 375mL", "Coke Zero", true)]
    [InlineData("Pepsi Max 375mL", "Pepsi", true)]
    [InlineData("Pepsi 375mL", "Pepsi Max", true)]
    [InlineData("Coca-Cola No Sugar", "Coca-Cola Classic", true)]
    [InlineData("Solo Sugar Free", "Solo", true)]
    [InlineData("Solo Sugar-Free", "Solo Sugarfree", false)]
    [InlineData("Diet Coke 375mL", "Coca-Cola Classic", true)]
    [InlineData("Coke Vanilla", "Coca-Cola Cherry", true)]
    [InlineData("Sprite Lime", "Sprite", true)]
    [InlineData("Coke Caffeine Free", "Coca-Cola Classic", true)]
    [InlineData("Nescafe Decaf", "Nescafe Decaffeinated", false)]
    [InlineData("Pepsi Light", "Pepsi Lite", false)]
    [InlineData("Coca-Cola Classic 375mL", "Coca-Cola Classic", false)]
    [InlineData("Coca-Cola Classic 375mL can", "Coca-Cola Classic", false)]
    [InlineData("Mount Franklin Still Water", "Mount Franklin Still Water", false)]
    public void A_descriptor_on_one_side_only_is_a_conflict(
        string searchName, string masterDescription, bool conflicts) =>
        Assert.Equal(
            conflicts,
            AutomaticProductMatchPolicy.HasConflictingDescriptor(searchName, masterDescription));

    /// <summary>
    /// The list is the issue's own list, and it is fixed: a test over its names is what makes a
    /// quietly added or quietly removed descriptor a visible change.
    /// </summary>
    [Fact]
    public void The_descriptor_list_is_the_fixed_reviewed_one() =>
        Assert.Equal(
            [
                "zero",
                "no sugar",
                "sugar free",
                "diet",
                "max",
                "light",
                "caffeine free",
                "decaf",
                "vanilla",
                "cherry",
                "lime",
                "original",
                "classic",
            ],
            AutomaticProductMatchPolicy.ConflictingDescriptors.Select(entry => entry.Name));

    #endregion

    #region One suitable candidate, decided here

    /// <summary>
    /// Everything agreeing: high confidence, a label that parses to the master product's own size, the
    /// brand and the variant in the search name, no descriptor on either side alone, one suitable
    /// candidate, and it is the one proposed.
    /// </summary>
    [Fact]
    public void An_obvious_match_is_applied()
    {
        var decision = Decide(ColaClassic.MasterProductId, candidates: [ColaClassic]);

        Assert.True(decision.IsApplied);
        Assert.Equal(ColaClassic.MasterProductId, decision.MasterProductId);
        Assert.Null(decision.Refusal);
    }

    /// <summary>Each condition failing on its own, with the reason it reports.</summary>
    [Theory]
    [InlineData(ProductMatchConfidence.Medium, "Coca-Cola Classic", "375mL",
        AutomaticProductMatchPolicy.ConfidenceBelowHighReason)]
    [InlineData(ProductMatchConfidence.Low, "Coca-Cola Classic", "375mL",
        AutomaticProductMatchPolicy.ConfidenceBelowHighReason)]
    [InlineData(ProductMatchConfidence.High, "Coca-Cola Classic", "can",
        AutomaticProductMatchPolicy.UnitLabelNotParsedReason)]
    [InlineData(ProductMatchConfidence.High, "Coca-Cola Classic", "355mL",
        AutomaticProductMatchPolicy.SizeDiffersReason)]
    [InlineData(ProductMatchConfidence.High, "Coca-Cola Classic", "0.375L",
        null)]
    [InlineData(ProductMatchConfidence.High, "Cola Classic", "375mL",
        AutomaticProductMatchPolicy.BrandMissingReason)]
    [InlineData(ProductMatchConfidence.High, "Coca-Cola", "375mL",
        AutomaticProductMatchPolicy.VariantDiffersReason)]
    [InlineData(ProductMatchConfidence.High, "Coca-Cola Classic Zero", "375mL",
        AutomaticProductMatchPolicy.ConflictingDescriptorReason)]
    public void Each_condition_refuses_on_its_own(
        ProductMatchConfidence confidence,
        string searchName,
        string unitLabel,
        string? refusal)
    {
        var decision = AutomaticProductMatchPolicy.Decide(
            searchName,
            unitLabel,
            ColaClassic.MasterProductId,
            confidence,
            [ColaClassic],
            new HashSet<long>());

        Assert.Equal(refusal, decision.Refusal);
        Assert.Equal(refusal is null, decision.IsApplied);
    }

    /// <summary>
    /// The pair the issue names: <c>Coke Zero 375 mL</c> is never matched to
    /// <c>Coke Classic 375 mL</c>, even at high confidence with the size and the brand agreeing.
    /// </summary>
    [Fact]
    public void Coke_zero_is_never_matched_to_coke_classic()
    {
        var cokeClassic = new ProductMatchCandidate(
            20, "Coke", "Classic", 375m, MasterProductSizeUnit.Millilitre);

        var decision = AutomaticProductMatchPolicy.Decide(
            "Coke Zero",
            "375mL",
            cokeClassic.MasterProductId,
            ProductMatchConfidence.High,
            [cokeClassic],
            new HashSet<long>());

        Assert.False(decision.IsApplied);
    }

    /// <summary>
    /// Two master products passing every check is an ambiguity, and a matcher proposing one of them
    /// does not resolve it: which of the two this product is is exactly what nobody has decided.
    /// </summary>
    [Fact]
    public void Two_suitable_master_products_apply_nothing_even_when_one_was_proposed()
    {
        // The same description recorded twice - which is what the master catalogue's unique key is
        // there to prevent and what a Super Admin has to clear up when it happens anyway.
        var duplicate = new ProductMatchCandidate(
            11, "Coca-Cola", "Classic", 0.375m, MasterProductSizeUnit.Litre);

        var decision = Decide(ColaClassic.MasterProductId, [ColaClassic, duplicate]);

        Assert.False(decision.IsApplied);
        Assert.Equal(
            AutomaticProductMatchPolicy.SeveralSuitableCandidatesReason,
            decision.Refusal);
    }

    /// <summary>
    /// A proposal that is not the suitable one is refused with that proposal's own failing condition,
    /// and the suitable master product is <strong>not</strong> applied in its place: the backend
    /// checks the matcher's answer, it does not replace it.
    /// </summary>
    [Fact]
    public void A_proposal_that_is_not_the_suitable_candidate_applies_nothing()
    {
        var pepsiMax = new ProductMatchCandidate(
            12, "Pepsi", "Max", 375m, MasterProductSizeUnit.Millilitre);

        var decision = Decide(pepsiMax.MasterProductId, [ColaClassic, pepsiMax]);

        Assert.False(decision.IsApplied);
        Assert.Equal(AutomaticProductMatchPolicy.BrandMissingReason, decision.Refusal);
    }

    /// <summary>
    /// A pair a Super Admin rejected or undid is never applied again, however obvious it looks - and
    /// it is not counted as a suitable candidate either, so refusing one master product does not stop
    /// another from being matched.
    /// </summary>
    [Fact]
    public void A_rejected_or_undone_pair_is_never_applied_again()
    {
        var decision = AutomaticProductMatchPolicy.Decide(
            "Coca-Cola Classic",
            "375mL",
            ColaClassic.MasterProductId,
            ProductMatchConfidence.High,
            [ColaClassic],
            new HashSet<long> { ColaClassic.MasterProductId });

        Assert.False(decision.IsApplied);
        Assert.Equal(AutomaticProductMatchPolicy.PairRefusedReason, decision.Refusal);
    }

    /// <summary>
    /// A refusal of one master product leaves another one matchable: the refusal is about a pair, not
    /// about the key.
    /// </summary>
    [Fact]
    public void A_refusal_of_one_master_product_leaves_the_other_matchable()
    {
        var refused = new ProductMatchCandidate(
            13, "Coca-Cola", "Classic", 0.375m, MasterProductSizeUnit.Litre);

        var decision = AutomaticProductMatchPolicy.Decide(
            "Coca-Cola Classic",
            "375mL",
            ColaClassic.MasterProductId,
            ProductMatchConfidence.High,
            [ColaClassic, refused],
            new HashSet<long> { refused.MasterProductId });

        Assert.True(decision.IsApplied);
        Assert.Equal(ColaClassic.MasterProductId, decision.MasterProductId);
    }

    /// <summary>A proposal naming a master product this catalogue does not hold applies nothing.</summary>
    [Fact]
    public void An_unknown_master_product_applies_nothing()
    {
        var decision = Decide(masterProductId: 999, candidates: [ColaClassic]);

        Assert.False(decision.IsApplied);
        Assert.Equal(AutomaticProductMatchPolicy.UnknownMasterProductReason, decision.Refusal);
    }

    /// <summary>
    /// A suggested <em>new</em> master product is never applied: creating one is a person's decision,
    /// which is this issue's own exclusion.
    /// </summary>
    [Fact]
    public void A_suggested_new_master_product_applies_nothing()
    {
        var decision = Decide(masterProductId: null, candidates: [ColaClassic]);

        Assert.False(decision.IsApplied);
        Assert.Equal(AutomaticProductMatchPolicy.SuggestedNewMasterReason, decision.Refusal);
    }

    /// <summary>An empty catalogue matches nothing, and says which condition refused it.</summary>
    [Fact]
    public void An_empty_catalogue_applies_nothing()
    {
        var decision = Decide(ColaClassic.MasterProductId, candidates: []);

        Assert.False(decision.IsApplied);
        Assert.Equal(AutomaticProductMatchPolicy.UnknownMasterProductReason, decision.Refusal);
    }

    #endregion

    #region The vocabulary and the batch bound

    /// <summary>
    /// The three levels are three exact stored proportions, and only the highest may apply anything.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchConfidence.High, 1.0, true)]
    [InlineData(ProductMatchConfidence.Medium, 0.6, false)]
    [InlineData(ProductMatchConfidence.Low, 0.3, false)]
    public void Each_confidence_level_has_its_own_stored_proportion(
        ProductMatchConfidence confidence, double proportion, bool automatic)
    {
        Assert.Equal(
            (decimal)proportion,
            AutomaticProductMatchPolicy.ConfidenceProportion(confidence));
        Assert.Equal(automatic, AutomaticProductMatchPolicy.IsAutomaticConfidence(confidence));

        // Every stored proportion is one the suggestion row accepts, which is what keeps the two
        // policies from disagreeing about what may be written.
        Assert.Equal(
            (decimal)proportion,
            ProductMatchSuggestionPolicy.NormaliseConfidence((decimal)proportion));
    }

    /// <summary>
    /// A confidence that is not one of the three declared levels is refused rather than treated as
    /// the first one: an enum is a compile-time constraint and a deserialized JSON number is not.
    /// </summary>
    [Fact]
    public void An_undeclared_confidence_is_refused()
    {
        Assert.False(AutomaticProductMatchPolicy.IsDeclaredConfidence((ProductMatchConfidence)7));
        Assert.Throws<DomainValidationException>(
            () => AutomaticProductMatchPolicy.ConfidenceProportion((ProductMatchConfidence)7));
    }

    /// <summary>Two hundred proposals are accepted and two hundred and one are refused whole.</summary>
    [Fact]
    public void The_batch_bound_is_two_hundred_proposals()
    {
        Assert.True(AutomaticProductMatchPolicy.IsAcceptedBatchSize(200));
        Assert.False(AutomaticProductMatchPolicy.IsAcceptedBatchSize(201));

        AutomaticProductMatchPolicy.RequireAcceptedBatchSize(200);

        var refusal = Assert.Throws<DomainValidationException>(
            () => AutomaticProductMatchPolicy.RequireAcceptedBatchSize(201));

        Assert.Equal(AutomaticProductMatchPolicy.BatchTooLargeMessage, refusal.Message);
    }

    #endregion

    private static AutomaticProductMatchDecision Decide(
        long? masterProductId,
        IReadOnlyCollection<ProductMatchCandidate> candidates) =>
        AutomaticProductMatchPolicy.Decide(
            "Coca-Cola Classic",
            "375mL",
            masterProductId,
            ProductMatchConfidence.High,
            candidates,
            new HashSet<long>());
}
