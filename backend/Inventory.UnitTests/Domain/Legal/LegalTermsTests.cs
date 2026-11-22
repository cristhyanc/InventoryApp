using Inventory.Domain.Exceptions;
using Inventory.Domain.Legal;
using Xunit;

namespace InventoryApi.Tests.Domain.Legal;

/// <summary>
/// The one rule about accepting the published Terms of Use and Privacy Policy (issue #1259): a
/// request carries the version in force, or it is refused with the sentence asking the person to
/// refresh.
///
/// <para>The comparison is deliberately exact. The value is machine-generated - the page sends back
/// what <c>GET /api/me/account-state</c> served it - so there is nothing to be forgiving about, and
/// a forgiving comparison is one the two sides could later disagree about.</para>
/// </summary>
public class LegalTermsTests
{
    /// <summary>
    /// The published version is an ISO-8601 calendar date, which is the form the request carries and
    /// the form the acceptance row stores. It is pinned here because the frontend's own contract
    /// test reads this constant and compares it with the effective date the published pages show;
    /// a version that stopped being a date would make that comparison meaningless rather than
    /// failing.
    /// </summary>
    [Fact]
    public void The_current_version_is_the_documents_effective_date_in_iso_form()
    {
        Assert.Equal("2026-11-13", LegalTerms.CurrentVersion);
    }

    [Fact]
    public void The_current_version_is_accepted_and_the_published_constant_is_what_is_returned()
    {
        // Deliberately a separately built string: what the caller sent must not be what is stored,
        // so this asserts the returned value is the constant rather than the submitted instance.
        var submitted = string.Join("-", "2026", "11", "13");

        Assert.True(LegalTerms.IsCurrent(submitted));
        Assert.Same(LegalTerms.CurrentVersion, LegalTerms.RequireCurrent(submitted));
    }

    /// <summary>
    /// Every way a request can fail to carry the current version - an old page that sends none at
    /// all, one that sends the previous version, and one whose value merely looks like it - and all
    /// of them get the one sentence, which asks for the only thing that helps.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-11-12")]
    [InlineData("2026-11-14")]
    [InlineData(" 2026-11-13")]
    [InlineData("2026-11-13 ")]
    [InlineData("13 November 2026")]
    [InlineData("2026-11-13T00:00:00Z")]
    public void Anything_but_the_current_version_is_refused_with_the_refresh_sentence(string? submitted)
    {
        Assert.False(LegalTerms.IsCurrent(submitted));

        var refusal = Assert.Throws<DomainValidationException>(() => LegalTerms.RequireCurrent(submitted));

        Assert.Equal(LegalTerms.StaleVersionMessage, refusal.Message);
    }

    /// <summary>
    /// The sentence itself, pinned: it is what the issue's acceptance criterion names word for word,
    /// it is what the frontend shows, and it names no version - which version a stale page held is
    /// not something the person who has to refresh can act on.
    /// </summary>
    [Fact]
    public void The_refusal_asks_for_a_refresh_and_names_no_version()
    {
        Assert.Equal(
            "Our Terms of Use have been updated. Please refresh the page and try again.",
            LegalTerms.StaleVersionMessage);
        Assert.DoesNotContain(LegalTerms.CurrentVersion, LegalTerms.StaleVersionMessage, StringComparison.Ordinal);
    }
}
