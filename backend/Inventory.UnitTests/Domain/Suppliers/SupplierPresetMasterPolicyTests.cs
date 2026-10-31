using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Xunit;

namespace Inventory.UnitTests.Domain.Suppliers;

/// <summary>
/// The supplier-master rules of one platform supplier preset (issue #931): the market it serves, the
/// tax basis of the prices it publishes, and the route those prices are collected by.
///
/// <para>Every rule here is about the consistency of a <em>claim</em>, so the cases that matter are
/// the combinations: nationwide together with a state, neither of the two, a key where no route reads
/// one, and a route that cannot be taken without one. Nothing here calculates a price or a GST
/// amount - the policy holds no amount at all - so there is no arithmetic to assert.</para>
/// </summary>
public class SupplierPresetMasterPolicyTests
{
    #region Served states

    /// <summary>
    /// The eight states and territories, closed and in the order a stored value is written in. The
    /// list is asserted rather than counted so adding a ninth is a deliberate change to this test as
    /// well as to the vocabulary.
    /// </summary>
    [Fact]
    public void The_served_state_vocabulary_is_the_eight_states_and_territories() =>
        Assert.Equal(
            ["NSW", "VIC", "QLD", "SA", "WA", "TAS", "ACT", "NT"],
            SupplierPresetMasterPolicy.ServedStateCodes);

    /// <summary>
    /// A nationwide preset stores no list at all. "Serves everywhere" is a different claim from
    /// "serves these eight", and storing the one as the other would lose the distinction the next
    /// territory to be added would need.
    /// </summary>
    [Fact]
    public void A_nationwide_preset_stores_no_state_list() =>
        Assert.Equal(
            string.Empty,
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: true, states: null));

    [Fact]
    public void A_nationwide_preset_may_be_submitted_with_an_empty_list() =>
        Assert.Equal(
            string.Empty,
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: true, states: []));

    /// <summary>
    /// One stored spelling per claim: the codes come back in the vocabulary's own order whatever
    /// order they were submitted in, so "ACT and NSW" and "NSW and ACT" are one value rather than
    /// two that every later reader would have to compare loosely.
    /// </summary>
    [Theory]
    [InlineData(new[] { "NSW" }, "NSW")]
    [InlineData(new[] { "NSW", "ACT" }, "NSW,ACT")]
    [InlineData(new[] { "ACT", "NSW" }, "NSW,ACT")]
    [InlineData(new[] { "NT", "SA" }, "SA,NT")]
    [InlineData(new[] { "NT", "ACT", "TAS", "WA", "SA", "QLD", "VIC", "NSW" }, "NSW,VIC,QLD,SA,WA,TAS,ACT,NT")]
    public void The_stored_state_list_is_in_the_vocabulary_s_order(string[] submitted, string expected) =>
        Assert.Equal(
            expected,
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, submitted));

    /// <summary>
    /// Case and padding are not part of the claim, and a repeated code is the same claim said twice:
    /// a form with eight checkboxes cannot produce one, and a caller who sends one has still said
    /// something unambiguous, so it is folded rather than refused.
    /// </summary>
    [Theory]
    [InlineData(new[] { " nsw ", "Act" }, "NSW,ACT")]
    [InlineData(new[] { "NSW", "nsw", "NSW" }, "NSW")]
    [InlineData(new[] { "SA", "", "  ", "NT" }, "SA,NT")]
    public void A_state_list_is_trimmed_upper_cased_and_de_duplicated(string[] submitted, string expected) =>
        Assert.Equal(
            expected,
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, submitted));

    /// <summary>
    /// The mutual exclusion. "Nationwide and NSW" has no meaning a price check could act on, so it is
    /// refused rather than resolved in either direction.
    /// </summary>
    [Fact]
    public void Nationwide_and_a_state_list_are_mutually_exclusive()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: true, ["NSW"]));

        Assert.Equal(SupplierPresetMasterPolicy.NationwideAndStatesMessage, error.Message);
    }

    /// <summary>
    /// And the other half of it: at least one. Neither claim would be a supplier that serves nowhere
    /// while looking configured, which is the state a price check must never be left to guess at.
    /// </summary>
    [Fact]
    public void A_preset_that_claims_neither_nationwide_nor_a_state_is_refused()
    {
        // No list at all, an empty one, and one holding nothing but blanks: three spellings of the
        // same omission, and all three have to be refused rather than one of them storing "nowhere".
        string?[][] nothingClaimed = [null!, [], ["   "], [null]];

        Assert.All(nothingClaimed, submitted =>
        {
            var error = Assert.Throws<DomainValidationException>(() =>
                SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, submitted));

            Assert.Equal(SupplierPresetMasterPolicy.ServedStatesRequiredMessage, error.Message);
        });
    }

    /// <summary>
    /// A state outside the vocabulary is refused rather than stored: "serves Jervis Bay" must not
    /// become a market nothing can describe. An enum would not have caught this - the value arrives
    /// as text from a JSON body.
    /// </summary>
    [Theory]
    [InlineData("NSWW")]
    [InlineData("New South Wales")]
    [InlineData("JBT")]
    [InlineData("NZ")]
    [InlineData("NATIONWIDE")]
    public void A_state_outside_the_vocabulary_is_refused(string submitted)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, [submitted]));

        Assert.Equal(SupplierPresetMasterPolicy.UnknownServedStateMessage, error.Message);
    }

    /// <summary>One unknown code refuses the whole claim; a partly stored market is not a market.</summary>
    [Fact]
    public void One_unknown_state_refuses_the_whole_list()
    {
        Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, ["NSW", "JBT"]));
    }

    /// <summary>
    /// Reading a stored value is the exact inverse of writing one, which is what keeps the column and
    /// the published list one fact rather than two.
    /// </summary>
    [Theory]
    [InlineData(new[] { "NSW", "ACT" }, "NSW,ACT")]
    [InlineData(new[] { "SA", "NT" }, "SA,NT")]
    [InlineData(new[] { "VIC" }, "VIC")]
    public void Reading_a_stored_list_gives_back_what_was_written(string[] submitted, string expectedStored)
    {
        var stored = SupplierPresetMasterPolicy.NormaliseServedStates(servesNationwide: false, submitted);
        Assert.Equal(expectedStored, stored);

        Assert.Equal(
            submitted.Order(StringComparer.Ordinal),
            SupplierPresetMasterPolicy.ReadServedStates(stored).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_stored_value_with_no_states_reads_as_no_states(string? stored) =>
        Assert.Empty(SupplierPresetMasterPolicy.ReadServedStates(stored));

    /// <summary>
    /// A read is tolerant where a write is strict, because what it reads is a column and not a
    /// submission: only a direct edit of the database could put an unknown code there, and publishing
    /// it as a state this application cannot describe would be worse than dropping it. The preset then
    /// reads as serving nowhere, which is visibly wrong on the page and is corrected by editing it -
    /// where the strict rules above apply in full.
    /// </summary>
    [Theory]
    [InlineData("NSW,JBT", new[] { "NSW" })]
    [InlineData("JBT", new string[0])]
    [InlineData(" act , nsw ", new[] { "NSW", "ACT" })]
    [InlineData("NSW,,ACT", new[] { "NSW", "ACT" })]
    public void A_stored_value_is_read_back_in_order_with_anything_unrecognised_dropped(
        string stored,
        string[] expected) =>
        Assert.Equal(expected, SupplierPresetMasterPolicy.ReadServedStates(stored));

    /// <summary>
    /// The column bound is computed from the vocabulary, so a ninth code could not leave the column
    /// too narrow for the longest value the rules permit.
    /// </summary>
    [Fact]
    public void The_stored_length_bound_fits_every_declared_state_at_once()
    {
        var everyState = SupplierPresetMasterPolicy.NormaliseServedStates(
            servesNationwide: false,
            SupplierPresetMasterPolicy.ServedStateCodes);

        Assert.Equal(everyState.Length, SupplierPresetMasterPolicy.MaxStoredServedStatesLength);
    }

    #endregion

    #region The price connector and its key

    /// <summary>
    /// The declared routes. They are named rather than counted because issue #931 settles these names
    /// for the collector work of #924 to consume, and BIG W is deliberately <em>one</em> route even
    /// though #926 reaches it through a name search with a SKU lookup behind it.
    /// </summary>
    [Fact]
    public void The_declared_connectors_are_the_four_collection_routes() =>
        Assert.Equal(
            ["Varnmill", "Kmart", "BigW", "JbMetro"],
            Enum.GetNames<SupplierPriceConnector>());

    /// <summary>No connector at all is a real state - the one a hand-checked supplier is in.</summary>
    [Fact]
    public void No_connector_is_a_declared_state() =>
        Assert.True(SupplierPresetMasterPolicy.IsDeclaredConnector(null));

    [Fact]
    public void Every_declared_connector_is_recognised() =>
        Assert.All(
            Enum.GetValues<SupplierPriceConnector>(),
            connector => Assert.True(SupplierPresetMasterPolicy.IsDeclaredConnector(connector)));

    /// <summary>
    /// An enum is a compile-time constraint, not a validation: a JSON number binds an arbitrary
    /// integer, and such a value is a route nothing can take rather than "no route".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(999)]
    public void An_undeclared_connector_number_is_refused(int submitted)
    {
        Assert.False(SupplierPresetMasterPolicy.IsDeclaredConnector((SupplierPriceConnector)submitted));

        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseConnectorKey((SupplierPriceConnector)submitted, "coles"));

        Assert.Equal(SupplierPresetMasterPolicy.UnknownConnectorMessage, error.Message);
    }

    /// <summary>Varnmill serves several retailers through one service, so its key names which one.</summary>
    [Fact]
    public void Varnmill_is_the_one_route_identified_by_a_key()
    {
        Assert.True(SupplierPresetMasterPolicy.TakesConnectorKey(SupplierPriceConnector.Varnmill));

        Assert.False(SupplierPresetMasterPolicy.TakesConnectorKey(SupplierPriceConnector.Kmart));
        Assert.False(SupplierPresetMasterPolicy.TakesConnectorKey(SupplierPriceConnector.BigW));
        Assert.False(SupplierPresetMasterPolicy.TakesConnectorKey(SupplierPriceConnector.JbMetro));
        Assert.False(SupplierPresetMasterPolicy.TakesConnectorKey(null));
    }

    [Theory]
    [InlineData("coles", "coles")]
    [InlineData("  woolworths  ", "woolworths")]
    [InlineData("aldi", "aldi")]
    public void A_varnmill_key_is_stored_trimmed(string submitted, string expected) =>
        Assert.Equal(
            expected,
            SupplierPresetMasterPolicy.NormaliseConnectorKey(SupplierPriceConnector.Varnmill, submitted));

    /// <summary>
    /// The key's own capitalisation is kept. It is an identifier the collector hands to a remote
    /// service, so upper-casing it the way a name is compared could change what it means.
    /// </summary>
    [Fact]
    public void A_varnmill_key_keeps_the_capitalisation_it_was_given() =>
        Assert.Equal(
            "Coles",
            SupplierPresetMasterPolicy.NormaliseConnectorKey(SupplierPriceConnector.Varnmill, "Coles"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Varnmill_without_a_key_is_refused(string? submitted)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseConnectorKey(SupplierPriceConnector.Varnmill, submitted));

        Assert.Equal(SupplierPresetMasterPolicy.ConnectorKeyRequiredMessage, error.Message);
    }

    [Fact]
    public void A_varnmill_key_longer_than_the_limit_is_refused()
    {
        var atTheLimit = new string('k', SupplierPresetMasterPolicy.MaxConnectorKeyLength);
        Assert.Equal(
            atTheLimit,
            SupplierPresetMasterPolicy.NormaliseConnectorKey(
                SupplierPriceConnector.Varnmill,
                $"  {atTheLimit}  "));

        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseConnectorKey(
                SupplierPriceConnector.Varnmill,
                new string('k', SupplierPresetMasterPolicy.MaxConnectorKeyLength + 1)));

        Assert.Equal(SupplierPresetMasterPolicy.ConnectorKeyTooLongMessage, error.Message);
    }

    /// <summary>
    /// A key where nothing reads one is a value that looks meaningful and is not, so it is refused
    /// rather than stored and ignored. "No route at all" is included deliberately: Costco has no
    /// connector, so it can have no key either.
    /// </summary>
    [Theory]
    [InlineData(SupplierPriceConnector.Kmart)]
    [InlineData(SupplierPriceConnector.BigW)]
    [InlineData(SupplierPriceConnector.JbMetro)]
    [InlineData(null)]
    public void A_key_for_a_route_that_takes_none_is_refused(SupplierPriceConnector? connector)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            SupplierPresetMasterPolicy.NormaliseConnectorKey(connector, "coles"));

        Assert.Equal(SupplierPresetMasterPolicy.ConnectorKeyNotAllowedMessage, error.Message);
    }

    /// <summary>
    /// And blank is "no key" for those routes rather than a refusal, stored as <c>null</c> so that
    /// "no key" has one spelling - exactly as "no website" does.
    /// </summary>
    [Theory]
    [InlineData(SupplierPriceConnector.Kmart, null)]
    [InlineData(SupplierPriceConnector.BigW, "")]
    [InlineData(SupplierPriceConnector.JbMetro, "   ")]
    [InlineData(null, null)]
    [InlineData(null, "  ")]
    public void A_route_that_takes_no_key_stores_none(SupplierPriceConnector? connector, string? submitted) =>
        Assert.Null(SupplierPresetMasterPolicy.NormaliseConnectorKey(connector, submitted));

    #endregion
}
