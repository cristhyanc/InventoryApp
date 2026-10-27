using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The closed vocabulary of Australian states and territories (issue #933): which values are one,
/// what a submitted value is normalised to, and what is refused.
///
/// <para>The eight codes are written out in this file rather than read from the type under test,
/// which is the point of the first test: a value quietly added to or removed from the vocabulary
/// changes which businesses the state-aware supplier work of #817 can reach, so it must not be
/// possible to make that change and still have a green build.</para>
/// </summary>
public class AustralianStatesTests
{
    [Fact]
    public void The_vocabulary_is_the_eight_states_and_territories_in_order()
    {
        Assert.Equal(["NSW", "VIC", "QLD", "SA", "WA", "TAS", "ACT", "NT"], AustralianStates.All);
    }

    [Fact]
    public void The_longest_code_fits_the_declared_bound()
    {
        Assert.All(AustralianStates.All, state => Assert.True(state.Length <= AustralianStates.MaxLength));
        Assert.Contains(AustralianStates.All, state => state.Length == AustralianStates.MaxLength);
    }

    [Theory]
    [InlineData("NSW", "NSW")]
    [InlineData("nsw", "NSW")]
    [InlineData("  qld  ", "QLD")]
    [InlineData("Act", "ACT")]
    [InlineData("nt", "NT")]
    public void A_submitted_code_is_normalised_to_its_canonical_abbreviation(string submitted, string expected)
    {
        Assert.True(AustralianStates.TryNormalize(submitted, out var normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>
    /// Anything else is refused rather than interpreted - a long name, a plausible-looking code
    /// that is not one of ours, another country's subdivision, and absence in each of its forms.
    /// Guessing which state somebody meant is how a business is recorded as trading somewhere it
    /// does not.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("New South Wales")]
    [InlineData("N.S.W.")]
    [InlineData("NSWW")]
    [InlineData("N")]
    [InlineData("AUS")]
    [InlineData("NZ")]
    [InlineData("CA")]
    [InlineData("JBT")]
    public void Anything_outside_the_vocabulary_is_refused(string? submitted)
    {
        Assert.False(AustralianStates.TryNormalize(submitted, out var normalized));
        Assert.Null(normalized);
        Assert.False(AustralianStates.IsValid(submitted));
    }

    /// <summary>
    /// <see cref="AustralianStates.IsValid"/> judges the <em>stored</em> form, so it accepts only
    /// the canonical abbreviation: the central enforcer uses it on the way into the database and
    /// must not quietly accept a value a write path forgot to normalise.
    /// </summary>
    [Theory]
    [InlineData("nsw")]
    [InlineData(" NSW")]
    [InlineData("NSW ")]
    public void A_value_that_is_merely_normalisable_is_not_a_stored_state(string stored)
    {
        Assert.False(AustralianStates.IsValid(stored));
        Assert.True(AustralianStates.TryNormalize(stored, out _));
    }

    [Fact]
    public void Every_canonical_code_is_valid_as_stored()
    {
        Assert.All(AustralianStates.All, state => Assert.True(AustralianStates.IsValid(state)));
    }

    /// <summary>
    /// The refusal names the rule and the eight values, so the person who typed something else can
    /// see what was expected, and carries nothing internal.
    /// </summary>
    [Fact]
    public void The_refusal_names_every_permitted_value()
    {
        Assert.All(
            AustralianStates.All,
            state => Assert.Contains(state, AustralianStates.InvalidMessage, StringComparison.Ordinal));
    }
}
