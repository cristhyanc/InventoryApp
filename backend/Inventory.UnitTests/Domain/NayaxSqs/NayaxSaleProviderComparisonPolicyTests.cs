using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// What comparing one stored SQS sale with the <c>lastSales</c> item carrying the same
/// <c>TransactionID</c> decides (issue #1104, child of #1100).
///
/// <para><strong>Every field is one of three answers, never two.</strong> A field is a match, a
/// mismatch, or <em>not compared</em> - and the third is what keeps a missing value from being
/// counted as agreement. That is the whole reason this policy exists as a value-in/value-out
/// function: "verified" has to mean "these specific fields were compared and were equal", which is
/// unanswerable if an absent value quietly reads as equal.</para>
///
/// <para>The normalisations are the owner's, from the issue's own table: the machine id exactly, the
/// amount rounded to two places half away from zero, the authorization instant as two UTC instants
/// equal within one second, the currency trimmed and upper-cased, and the Nayax product id
/// exactly.</para>
/// </summary>
public class NayaxSaleProviderComparisonPolicyTests
{
    private static readonly DateTime Authorized = new(2026, 11, 7, 2, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void Every_compared_field_matching_verifies_the_sale()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(Stored(), Provider());

        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Machine);
        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Amount);
        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Instant);
        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Currency);
        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Product);
        Assert.Equal(NayaxSqsVerification.Verified, result.ObservedState);
    }

    [Fact]
    public void A_different_machine_is_an_authoritative_contradiction()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored(), Provider() with { MachineId = 99 });

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, result.Fields.Machine);
        Assert.Equal(NayaxSqsVerification.Conflict, result.ObservedState);
    }

    /// <summary>
    /// The half-cent, which is the whole reason the rounding mode is written down: banker's rounding
    /// would make <c>1.005</c> and <c>1.00</c> agree and <c>1.015</c> and <c>1.02</c> agree, so two
    /// amounts that differ by a cent would be a match in one direction and a mismatch in the other.
    /// </summary>
    [Theory]
    [InlineData(1.005, 1.01, true)]
    [InlineData(1.005, 1.00, false)]
    [InlineData(2.675, 2.68, true)]
    [InlineData(-1.005, -1.01, true)]
    [InlineData(3.5, 3.50, true)]
    public void An_amount_is_compared_rounded_to_two_places_half_away_from_zero(
        double stored, double provider, bool matches)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { SettlementValue = (decimal)stored },
            Provider() with { SettlementValue = (decimal)provider });

        Assert.Equal(
            matches ? NayaxSaleFieldComparison.Match : NayaxSaleFieldComparison.Mismatch,
            result.Fields.Amount);
    }

    [Fact]
    public void A_different_amount_is_an_authoritative_contradiction()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored(), Provider() with { SettlementValue = 4.00m });

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, result.Fields.Amount);
        Assert.Equal(NayaxSqsVerification.Conflict, result.ObservedState);
    }

    [Theory]
    [InlineData(900, NayaxSaleFieldComparison.Match)]
    [InlineData(-900, NayaxSaleFieldComparison.Match)]
    [InlineData(1000, NayaxSaleFieldComparison.Match)]
    [InlineData(1100, NayaxSaleFieldComparison.Mismatch)]
    [InlineData(-1100, NayaxSaleFieldComparison.Mismatch)]
    public void An_instant_matches_while_it_is_no_more_than_a_second_apart(
        int offsetMilliseconds, NayaxSaleFieldComparison expected)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored(),
            Provider() with
            {
                AuthorizationInstantUtc = Authorized.AddMilliseconds(offsetMilliseconds),
            });

        Assert.Equal(expected, result.Fields.Instant);
    }

    /// <summary>
    /// A differing instant is <em>not</em> a contradiction about what was sold: the queue message and
    /// the provider's own record can disagree about when a transaction was authorized while agreeing
    /// about the machine, the amount and the product. It is flagged for a person instead.
    /// </summary>
    [Fact]
    public void A_differing_instant_needs_investigation_rather_than_conflicting()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored(), Provider() with { AuthorizationInstantUtc = Authorized.AddMinutes(5) });

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, result.Fields.Instant);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, result.ObservedState);
    }

    [Theory]
    [InlineData("aud", "AUD")]
    [InlineData("  AUD  ", "AUD")]
    [InlineData("AUD", " aud\t")]
    public void A_currency_is_compared_trimmed_and_upper_cased(string stored, string provider)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { CurrencyCode = stored }, Provider() with { CurrencyCode = provider });

        Assert.Equal(NayaxSaleFieldComparison.Match, result.Fields.Currency);
        Assert.Equal(NayaxSqsVerification.Verified, result.ObservedState);
    }

    [Fact]
    public void A_differing_currency_needs_investigation_rather_than_conflicting()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { CurrencyCode = "AUD" }, Provider() with { CurrencyCode = "USD" });

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, result.Fields.Currency);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, result.ObservedState);
    }

    /// <summary>
    /// The case the issue names explicitly: a product absent on one side is not compared, and the
    /// sale is still verified by the three fields that were.
    /// </summary>
    [Theory]
    [InlineData(null, 55L)]
    [InlineData(55L, null)]
    [InlineData(null, null)]
    public void A_product_present_on_only_one_side_is_not_compared_and_still_verifies(
        long? stored, long? provider)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { NayaxProductId = stored },
            Provider() with { NayaxProductId = provider });

        Assert.Equal(NayaxSaleFieldComparison.NotCompared, result.Fields.Product);
        Assert.Equal(NayaxSqsVerification.Verified, result.ObservedState);
    }

    [Fact]
    public void A_different_product_is_an_authoritative_contradiction()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { NayaxProductId = 55 }, Provider() with { NayaxProductId = 56 });

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, result.Fields.Product);
        Assert.Equal(NayaxSqsVerification.Conflict, result.ObservedState);
    }

    [Theory]
    [InlineData(null, "AUD")]
    [InlineData("AUD", null)]
    [InlineData("", "AUD")]
    [InlineData("   ", "AUD")]
    [InlineData("AUD", "")]
    public void A_currency_missing_on_either_side_is_not_compared_and_still_verifies(
        string? stored, string? provider)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { CurrencyCode = stored }, Provider() with { CurrencyCode = provider });

        Assert.Equal(NayaxSaleFieldComparison.NotCompared, result.Fields.Currency);
        Assert.Equal(NayaxSqsVerification.Verified, result.ObservedState);
    }

    /// <summary>
    /// An instant missing on either side cannot verify anything: the issue's own definition of
    /// <c>Verified</c> requires the machine, the amount <em>and</em> the instant to have been
    /// compared. With no instant to compare, the comparison decides no state at all - the sale keeps
    /// whatever state it had - and the evidence still records what was seen.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void An_instant_missing_on_either_side_decides_no_state(bool storedHas, bool providerHas)
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { AuthorizationInstantUtc = storedHas ? Authorized : null },
            Provider() with { AuthorizationInstantUtc = providerHas ? Authorized : null });

        Assert.Equal(NayaxSaleFieldComparison.NotCompared, result.Fields.Instant);
        Assert.Null(result.ObservedState);
    }

    /// <summary>
    /// A contradiction wins over a flag, and both win over a match: a sale whose amount disagrees and
    /// whose currency also disagrees is a conflict, not an investigation.
    /// </summary>
    [Fact]
    public void A_contradiction_outranks_a_field_that_only_needs_investigation()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { CurrencyCode = "AUD" },
            Provider() with { SettlementValue = 9.99m, CurrencyCode = "USD" });

        Assert.Equal(NayaxSqsVerification.Conflict, result.ObservedState);
    }

    /// <summary>
    /// A mismatch is never a match, however the state came out. This is the rule the issue states as
    /// "a field that cannot be compared is never counted as a match"; the mirror of it is that a
    /// field that was compared and differed is never reported as not compared either.
    /// </summary>
    [Fact]
    public void A_not_compared_field_is_never_reported_as_a_match()
    {
        var result = NayaxSaleProviderComparisonPolicy.Compare(
            Stored() with { CurrencyCode = null, NayaxProductId = null },
            Provider() with { CurrencyCode = null, NayaxProductId = null, ProductName = null });

        Assert.DoesNotContain(
            NayaxSaleFieldComparison.Match,
            new[] { result.Fields.Currency, result.Fields.Product });
    }

    private static NayaxStoredSaleValues Stored() => new(
        MachineId: 42,
        SettlementValue: 3.50m,
        AuthorizationInstantUtc: Authorized,
        CurrencyCode: "AUD",
        NayaxProductId: 55);

    private static NayaxProviderSaleValues Provider() => new(
        MachineId: 42,
        SettlementValue: 3.50m,
        AuthorizationInstantUtc: Authorized,
        CurrencyCode: "AUD",
        NayaxProductId: 55,
        ProductName: "Spring Water 600ml");
}
