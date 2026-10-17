using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// The one Nayax id ↔ <c>ExternalId</c> conversion of issue #732.
///
/// It is small on purpose, and pinned anyway: every link written by the backfill, by the catalogue
/// import and by the later lookups (#733, #745, #747, #734) is matched as exact text, so a change
/// in how an id is written - a separator, a culture, a sign - would not fail here, it would make
/// one business's links silently stop resolving.
/// </summary>
public class NayaxExternalIdTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(100L)]
    [InlineData(1234567L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void An_id_round_trips_through_the_external_id_text(long nayaxId)
    {
        var externalId = NayaxExternalId.From(nayaxId);

        Assert.True(NayaxExternalId.TryParse(externalId, out var parsed));
        Assert.Equal(nayaxId, parsed);
    }

    /// <summary>
    /// The written form is the invariant decimal one, stated explicitly rather than only via the
    /// round trip above: it is also what the backfill migration's <c>CAST(Id AS TEXT)</c> produces,
    /// and the two have to agree for a backfilled link to resolve.
    /// </summary>
    [Theory]
    [InlineData(7L, "7")]
    [InlineData(1000L, "1000")]
    [InlineData(9223372036854775807L, "9223372036854775807")]
    public void The_external_id_is_the_invariant_decimal_form(long nayaxId, string expected) =>
        Assert.Equal(expected, NayaxExternalId.From(nayaxId));

    /// <summary>
    /// An <c>ExternalId</c> is opaque text, so anything may be stored in the column - including, one
    /// provider later, something that is not a number at all. Reading such a value as a Nayax id
    /// must fail rather than land on a nearby one, because the caller is about to treat the answer
    /// as the identity of a product.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 7")]
    [InlineData("7 ")]
    [InlineData("7.0")]
    [InlineData("1,000")]
    [InlineData("0x10")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("7a")]
    [InlineData("9223372036854775808")]
    public void Text_no_nayax_id_could_have_been_written_as_is_refused(string? externalId)
    {
        Assert.False(NayaxExternalId.TryParse(externalId, out var parsed));
        Assert.Equal(0, parsed);
    }
}
