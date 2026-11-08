using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of a master product's supplier search terms (issue #1138): what one entry may hold, how
/// its text is cleaned, and which entry a work item for one supplier receives.
/// </summary>
public class SupplierSearchTermsPolicyTests
{
    private const int JbMetroBankstown = 7;
    private const int JbMetroParramatta = 8;
    private const int Coles = 3;

    #region Cleaning

    [Fact]
    public void No_entries_is_an_empty_list()
    {
        Assert.Empty(SupplierSearchTermsPolicy.Normalise(null));
        Assert.Empty(SupplierSearchTermsPolicy.Normalise([]));
    }

    /// <summary>
    /// Phrases are trimmed, inner spaces collapsed, blanks dropped and a repeat that differs only by
    /// case dropped, keeping the first spelling and the order a person chose - so no query repeats.
    /// </summary>
    [Fact]
    public void Phrases_and_aliases_are_trimmed_deduplicated_and_kept_in_order()
    {
        var entry = Assert.Single(SupplierSearchTermsPolicy.Normalise(
        [
            Connector(
                SupplierPriceConnector.JbMetro,
                ["  Smiths   Plain ", "", null, "SMITHS PLAIN", "Smiths"],
                [" Plain ", "plain", "  "]),
        ]));

        Assert.Equal(["Smiths Plain", "Smiths"], entry.SearchPhrases);
        Assert.Equal(["Plain"], entry.FlavourAliases);
        Assert.Equal(SupplierPriceConnector.JbMetro, entry.PriceConnector);
        Assert.Null(entry.SupplierPresetId);
    }

    /// <summary>The collector never searches more than two wordings, so a third phrase is refused.</summary>
    [Fact]
    public void A_third_distinct_phrase_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
            [Connector(SupplierPriceConnector.JbMetro, ["Smiths Plain", "Smiths Chips", "Smiths"], [])]));

        Assert.Equal(SupplierSearchTermsPolicy.TooManyPhrasesMessage, refusal.Message);
    }

    /// <summary>A repeat is not a third phrase: it is dropped before the count is taken.</summary>
    [Fact]
    public void A_repeated_phrase_does_not_count_towards_the_limit() =>
        Assert.Equal(
            ["Smiths Plain", "Smiths"],
            Assert.Single(SupplierSearchTermsPolicy.Normalise(
                [Connector(SupplierPriceConnector.JbMetro, ["Smiths Plain", "Smiths", "smiths"], [])]))
                .SearchPhrases);

    [Fact]
    public void Aliases_alone_are_an_entry_but_nothing_at_all_is_not()
    {
        var aliasesOnly = Assert.Single(SupplierSearchTermsPolicy.Normalise(
            [Preset(Coles, [], ["Plain"])]));
        Assert.Empty(aliasesOnly.SearchPhrases);

        var refusal = Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
            [Preset(Coles, [" ", null], [""])]));
        Assert.Equal(SupplierSearchTermsPolicy.EmptyEntryMessage, refusal.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3, SupplierPriceConnector.Varnmill)]
    public void An_entry_names_exactly_one_target(int? presetId, SupplierPriceConnector? connector)
    {
        var refusal = Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
            [new SupplierSearchTermsSubmission(presetId, connector, ["Smiths"], [])]));

        Assert.Equal(SupplierSearchTermsPolicy.TargetRequiredMessage, refusal.Message);
    }

    [Fact]
    public void An_undeclared_connector_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
            [Connector((SupplierPriceConnector)99, ["Smiths"], [])]));

        Assert.Equal(SupplierSearchTermsPolicy.UnknownConnectorMessage, refusal.Message);
    }

    [Fact]
    public void One_target_holds_one_entry()
    {
        Assert.Equal(
            SupplierSearchTermsPolicy.DuplicateTargetMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(Coles, ["Smiths"], []), Preset(Coles, ["Smith's"], [])])).Message);

        Assert.Equal(
            SupplierSearchTermsPolicy.DuplicateTargetMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
            [
                Connector(SupplierPriceConnector.JbMetro, ["Smiths"], []),
                Connector(SupplierPriceConnector.JbMetro, ["Smiths Plain"], []),
            ])).Message);

        // A preset and its own connector are two targets, not a duplicate: the preset's entry wins.
        Assert.Equal(2, SupplierSearchTermsPolicy.Normalise(
        [
            Connector(SupplierPriceConnector.JbMetro, ["Smiths"], []),
            Preset(JbMetroBankstown, ["Smiths Plain"], []),
        ]).Count);
    }

    [Fact]
    public void Text_that_cannot_be_a_search_is_refused()
    {
        Assert.Equal(
            SupplierSearchTermsPolicy.PhraseTooLongMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(Coles, [new string('a', SupplierSearchTermsPolicy.MaxSearchPhraseLength + 1)], [])])).Message);

        Assert.Equal(
            SupplierSearchTermsPolicy.AliasTooLongMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(Coles, [], [new string('a', SupplierSearchTermsPolicy.MaxFlavourAliasLength + 1)])])).Message);

        Assert.Equal(
            SupplierSearchTermsPolicy.ControlCharacterMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(Coles, ["Smiths\nPlain"], [])])).Message);

        Assert.Equal(
            SupplierSearchTermsPolicy.TooManyAliasesMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(Coles, [], ["a", "b", "c", "d", "e", "f"])])).Message);

        Assert.Equal(
            SupplierSearchTermsPolicy.UnknownSupplierPresetMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(
                [Preset(0, ["Smiths"], [])])).Message);
    }

    [Fact]
    public void A_master_product_holds_a_bounded_number_of_entries()
    {
        var entries = Enumerable.Range(1, SupplierSearchTermsPolicy.MaxEntriesPerMasterProduct + 1)
            .Select(id => (SupplierSearchTermsSubmission?)Preset(id, ["Smiths"], []))
            .ToList();

        Assert.Equal(
            SupplierSearchTermsPolicy.TooManyEntriesMessage,
            Assert.Throws<DomainValidationException>(() => SupplierSearchTermsPolicy.Normalise(entries)).Message);
    }

    #endregion

    #region Which entry applies

    /// <summary>One JB Metro connector entry reaches every JB Metro site and no other connector.</summary>
    [Fact]
    public void A_connector_entry_reaches_every_preset_on_that_connector_and_no_other()
    {
        var entries = SupplierSearchTermsPolicy.Normalise(
            [Connector(SupplierPriceConnector.JbMetro, ["Smiths Plain", "Smiths"], ["Plain"])]);

        Assert.Equal(
            ["Smiths Plain", "Smiths"],
            SupplierSearchTermsPolicy.EffectiveFor(entries, JbMetroBankstown, SupplierPriceConnector.JbMetro)!
                .SearchPhrases);
        Assert.NotNull(SupplierSearchTermsPolicy.EffectiveFor(entries, JbMetroParramatta, SupplierPriceConnector.JbMetro));
        Assert.Null(SupplierSearchTermsPolicy.EffectiveFor(entries, Coles, SupplierPriceConnector.Varnmill));
        Assert.Null(SupplierSearchTermsPolicy.EffectiveFor(entries, Coles, null));
    }

    /// <summary>
    /// A preset's own entry replaces its connector's entry as a whole - nothing is merged - and only
    /// for that preset; the other sites on the connector still receive the connector entry.
    /// </summary>
    [Fact]
    public void A_preset_entry_takes_precedence_over_its_connector_entry_as_a_whole()
    {
        var entries = SupplierSearchTermsPolicy.Normalise(
        [
            Connector(SupplierPriceConnector.JbMetro, ["Smiths Plain", "Smiths"], ["Plain"]),
            Preset(JbMetroBankstown, [], ["Original"]),
        ]);

        var bankstown = SupplierSearchTermsPolicy.EffectiveFor(entries, JbMetroBankstown, SupplierPriceConnector.JbMetro)!;
        Assert.Empty(bankstown.SearchPhrases);
        Assert.Equal(["Original"], bankstown.FlavourAliases);

        var parramatta = SupplierSearchTermsPolicy.EffectiveFor(entries, JbMetroParramatta, SupplierPriceConnector.JbMetro)!;
        Assert.Equal(["Smiths Plain", "Smiths"], parramatta.SearchPhrases);
    }

    [Fact]
    public void A_product_without_entries_has_no_effective_entry()
    {
        Assert.Null(SupplierSearchTermsPolicy.EffectiveFor(null, Coles, SupplierPriceConnector.Varnmill));
        Assert.Null(SupplierSearchTermsPolicy.EffectiveFor([], Coles, SupplierPriceConnector.Varnmill));
    }

    #endregion

    private static SupplierSearchTermsSubmission Preset(int id, string?[] phrases, string?[] aliases) =>
        new(id, null, phrases, aliases);

    private static SupplierSearchTermsSubmission Connector(
        SupplierPriceConnector connector,
        string?[] phrases,
        string?[] aliases) =>
        new(null, connector, phrases, aliases);
}
