using Inventory.Domain.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Domain.Suppliers;

/// <summary>
/// The one rule that decides whether a platform supplier preset is a supplier record a given
/// business may receive (issue #935, third task of #817): the state-matching rule.
///
/// <para>Every assertion is written against the three facts the rule reads - whether the preset is
/// still suggested, what market it claims, and the state the business trades in - because those are
/// the only facts there are. A business that holds a copy already, and which business a copy is
/// written into, are both somebody else's question: this policy reaches no supplier row.</para>
/// </summary>
public class SupplierPresetMarketPolicyTests
{
    /// <summary>
    /// A nationwide record serves every state, and a business with no recorded state as well. It is
    /// the one claim that covers a business whose state nobody has recorded, which is exactly why a
    /// nationwide preset stores no state list rather than all eight codes.
    /// </summary>
    [Theory]
    [InlineData("NSW")]
    [InlineData("ACT")]
    [InlineData("NT")]
    [InlineData(null)]
    public void A_nationwide_record_serves_every_business(string? businessState) =>
        Assert.True(SupplierPresetMarketPolicy.ServesState(
            servesNationwide: true,
            storedServedStates: string.Empty,
            businessState));

    /// <summary>
    /// A record that names states serves exactly those, matched against the canonical stored codes.
    /// "JB Metro South Coast &amp; ACT" is the case the issue is about: a Canberra business is served
    /// and a Perth one is not.
    /// </summary>
    [Theory]
    [InlineData("NSW,ACT", "ACT", true)]
    [InlineData("NSW,ACT", "NSW", true)]
    [InlineData("NSW,ACT", "WA", false)]
    [InlineData("NSW", "NSW", true)]
    [InlineData("NSW", "VIC", false)]
    public void A_record_that_names_states_serves_exactly_those(
        string storedServedStates,
        string businessState,
        bool expected) =>
        Assert.Equal(
            expected,
            SupplierPresetMarketPolicy.ServesState(
                servesNationwide: false,
                storedServedStates,
                businessState));

    /// <summary>
    /// A business with no recorded state - every business that existed before issue #933's column
    /// did - is served only by a nationwide record. It is never matched to a state record, because
    /// there is no state to match: guessing one from a time zone, an address or an ABN is exactly
    /// what <c>AustralianStates</c> refuses to do.
    /// </summary>
    [Theory]
    [InlineData("NSW")]
    [InlineData("NSW,ACT")]
    [InlineData("")]
    public void A_business_with_no_recorded_state_is_served_only_by_a_nationwide_record(
        string storedServedStates) =>
        Assert.False(SupplierPresetMarketPolicy.ServesState(
            servesNationwide: false,
            storedServedStates,
            businessState: null));

    /// <summary>
    /// A record with no recorded market at all - the fail-closed default a row the supplier-master
    /// migration did not recognise carries - serves nobody. It reads as "not set" on the Super
    /// Admin's page and is corrected by editing it there, which is where the market rules apply in
    /// full; until then no business receives it, because a record that claims no market has not
    /// claimed this business's.
    /// </summary>
    [Theory]
    [InlineData("NSW")]
    [InlineData(null)]
    public void A_record_with_no_recorded_market_serves_nobody(string? businessState) =>
        Assert.False(SupplierPresetMarketPolicy.ServesState(
            servesNationwide: false,
            storedServedStates: string.Empty,
            businessState));

    /// <summary>
    /// An unrecognised stored code - which only a direct edit of the database could produce - is
    /// dropped by the master policy's tolerant read, so it matches nothing. The record reads as
    /// serving nowhere rather than as serving a state this application cannot describe.
    /// </summary>
    [Fact]
    public void An_unrecognised_stored_code_matches_nothing() =>
        Assert.False(SupplierPresetMarketPolicy.ServesState(
            servesNationwide: false,
            storedServedStates: "JBT",
            businessState: "JBT"));

    /// <summary>
    /// A record is offered only while it is <em>both</em> suggested and serving the business. A
    /// disabled record is offered to nobody, however well its market matches - and the two halves are
    /// asserted separately, because a rule that read only one of them would still pass a test that
    /// varied both at once.
    /// </summary>
    [Theory]
    [InlineData(true, true, "NSW", true)]
    [InlineData(false, true, "NSW", false)]
    [InlineData(true, false, "NSW", true)]
    [InlineData(false, false, "NSW", false)]
    [InlineData(true, false, "WA", false)]
    [InlineData(false, false, "WA", false)]
    public void A_record_is_offered_only_while_it_is_enabled_and_serves_the_business(
        bool isEnabled,
        bool servesNationwide,
        string businessState,
        bool expected) =>
        Assert.Equal(
            expected,
            SupplierPresetMarketPolicy.IsOfferedTo(
                isEnabled,
                servesNationwide,
                storedServedStates: servesNationwide ? string.Empty : "NSW,ACT",
                businessState));

    /// <summary>
    /// The refusal a second copy of one record is answered with exists, says what to do about it,
    /// and names no business: it is the sentence the <c>409</c> carries when a double-click, two tabs
    /// or a state change racing an add asks for a copy that is already there.
    /// </summary>
    [Fact]
    public void The_already_added_refusal_is_one_sentence_about_the_supplier()
    {
        Assert.False(string.IsNullOrWhiteSpace(SupplierPresetMarketPolicy.AlreadyAddedMessage));
        Assert.DoesNotContain("business", SupplierPresetMarketPolicy.AlreadyAddedMessage, StringComparison.OrdinalIgnoreCase);
    }
}
