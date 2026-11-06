using Inventory.Domain.NayaxSqs;
using Xunit;

namespace Inventory.UnitTests.Domain.NayaxSqs;

/// <summary>
/// The rules a business's Nayax SQS sender identity is recorded by (issue #1101): what a sender id
/// has to look like, and the one normalisation between what an administrator pastes and what is
/// stored.
///
/// <para>Every case here is a pure function of its arguments. The clock, the actor, the revision,
/// the lifecycle gate and the database belong to the use case that calls these rules, which is what
/// makes the boundary cases below answerable at the character rather than only through HTTP.</para>
/// </summary>
public class NayaxSqsSenderPolicyTests
{
    /// <summary>
    /// The shape AWS actually reports as an SQS <c>SenderId</c>: a prefixed unique id. The real
    /// example from the issue, and the two boundary lengths beside it.
    /// </summary>
    [Theory]
    [InlineData("AIDAEXAMPLEUSERID123")]
    [InlineData("AIDA123456789012")]
    [InlineData("aida123456789012")]
    [InlineData("AIDAIOSFODNN7EXAMPLE")]
    public void A_sender_id_of_letters_and_digits_within_the_bounds_is_well_formed(string senderId) =>
        Assert.True(NayaxSqsSenderPolicy.IsWellFormedSenderId(senderId));

    [Fact]
    public void The_shortest_and_longest_accepted_ids_are_exactly_the_declared_bounds()
    {
        Assert.True(NayaxSqsSenderPolicy.IsWellFormedSenderId(
            new string('A', NayaxSqsSenderPolicy.MinSenderIdLength)));
        Assert.True(NayaxSqsSenderPolicy.IsWellFormedSenderId(
            new string('A', NayaxSqsSenderPolicy.MaxSenderIdLength)));

        Assert.False(NayaxSqsSenderPolicy.IsWellFormedSenderId(
            new string('A', NayaxSqsSenderPolicy.MinSenderIdLength - 1)));
        Assert.False(NayaxSqsSenderPolicy.IsWellFormedSenderId(
            new string('A', NayaxSqsSenderPolicy.MaxSenderIdLength + 1)));
    }

    /// <summary>
    /// Everything a paste can go wrong as: too short, punctuation, a space in the middle, an ARN
    /// rather than a unique id, and <see langword="null"/>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AIDASHORT")]
    [InlineData("AIDA-EXAMPLE-USER-123")]
    [InlineData("AIDA EXAMPLE USERID1")]
    [InlineData("arn:aws:iam::123456789012:user/restockroo")]
    [InlineData("AIDAEXAMPLEUSERID12_")]
    public void A_sender_id_that_is_not_letters_and_digits_within_the_bounds_is_refused(string? senderId) =>
        Assert.False(NayaxSqsSenderPolicy.IsWellFormedSenderId(senderId));

    /// <summary>
    /// The specific confusable this rule exists for: a Devanagari digit and a Cyrillic letter are
    /// each a letter or digit to Unicode, and neither is an AWS unique id. Accepting them would let
    /// two visually identical senders be recorded for two businesses, which the global unique index
    /// would be right to allow and a person would be wrong to trust.
    /// </summary>
    [Theory]
    [InlineData("AIDAEXAMPLEUSERID१२३")]
    [InlineData("АIDAEXAMPLEUSERID123")]
    public void A_non_ascii_letter_or_digit_is_not_a_sender_id(string senderId) =>
        Assert.False(NayaxSqsSenderPolicy.IsWellFormedSenderId(senderId));

    [Theory]
    [InlineData("  AIDAEXAMPLEUSERID123  ", "AIDAEXAMPLEUSERID123")]
    [InlineData("AIDAEXAMPLEUSERID123", "AIDAEXAMPLEUSERID123")]
    public void A_sender_id_is_stored_trimmed_and_otherwise_exactly_as_entered(
        string entered,
        string stored) =>
        Assert.Equal(stored, NayaxSqsSenderPolicy.NormaliseSenderId(entered));

    /// <summary>
    /// Whitespace is no sender rather than a blank one: "remove" and "a field somebody cleared" have
    /// to be the same single stored representation, or a row could record an enabled mapping that
    /// matches nothing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Nothing_at_all_normalises_to_no_sender(string? entered) =>
        Assert.Null(NayaxSqsSenderPolicy.NormaliseSenderId(entered));

    /// <summary>
    /// The refusals are caller-safe sentences: each says what the rule is, and the duplicate one in
    /// particular names no business and hints at none, because which business holds an identity is
    /// not a fact this surface may publish.
    /// </summary>
    [Fact]
    public void The_refusals_name_the_rule_and_never_another_business()
    {
        Assert.Contains("letters and digits", NayaxSqsSenderPolicy.InvalidSenderIdMessage, StringComparison.Ordinal);
        Assert.Contains("16", NayaxSqsSenderPolicy.InvalidSenderIdMessage, StringComparison.Ordinal);
        Assert.Contains("128", NayaxSqsSenderPolicy.InvalidSenderIdMessage, StringComparison.Ordinal);

        Assert.Contains("another business", NayaxSqsSenderPolicy.DuplicateSenderIdMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("AIDA", NayaxSqsSenderPolicy.DuplicateSenderIdMessage, StringComparison.Ordinal);

        Assert.All(
            new[]
            {
                NayaxSqsSenderPolicy.InvalidSenderIdMessage,
                NayaxSqsSenderPolicy.EnabledNeedsSenderIdMessage,
                NayaxSqsSenderPolicy.StaleRevisionMessage,
                NayaxSqsSenderPolicy.DuplicateSenderIdMessage,
                NayaxSqsSenderPolicy.BusinessNotActiveMessage,
            },
            message => Assert.False(string.IsNullOrWhiteSpace(message)));
    }
}
