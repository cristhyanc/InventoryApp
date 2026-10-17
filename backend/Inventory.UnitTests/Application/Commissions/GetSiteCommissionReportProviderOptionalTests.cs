using Inventory.Application.Commissions;
using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Application.SupportSessions;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Commissions;

/// <summary>
/// Issue #731 (parent #329 decision P9): the site commission report answers "unavailable" when the
/// provider is not usable instead of failing the request.
///
/// <para>A site is known only from the live fleet read's <c>CustomerID</c>, so an unusable provider
/// leaves nothing to calculate - and because the bookkeeping, GST, dashboard and machine
/// profitability reports all compose this one, an exception here used to take all of them down with
/// it. These tests are about the line between an <em>availability</em> answer and a
/// <em>failure</em>: exactly two refusals degrade, and everything else propagates, so an outage is
/// never published as a business whose sites are owed nothing.</para>
/// </summary>
public class GetSiteCommissionReportProviderOptionalTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    [Fact]
    public async Task A_business_with_no_stored_connection_gets_an_unavailable_report_rather_than_an_error()
    {
        var report = await Handle(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured)));

        Assert.False(report.IsAvailable);
        Assert.Equal(SiteCommissionUnavailableReason.NayaxNotConnected, report.UnavailableReason);
        Assert.Empty(report.Rows);
        Assert.Equal(From, report.From);
        Assert.Equal(To, report.To);
    }

    /// <summary>
    /// Stored credentials the status gate refuses are the same answer: what the operator does about
    /// it - connect or reconnect Nayax - is the same, so one reason covers both.
    /// </summary>
    [Fact]
    public async Task A_connection_that_needs_attention_is_unavailable_for_the_same_reason()
    {
        var report = await Handle(FleetRefusing(new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention)));

        Assert.False(report.IsAvailable);
        Assert.Equal(SiteCommissionUnavailableReason.NayaxNotConnected, report.UnavailableReason);
    }

    [Fact]
    public async Task A_permission_refusal_is_unavailable_with_its_own_reason()
    {
        var report = await Handle(FleetRefusing(new NayaxPermissionNotGrantedException("machines")));

        Assert.False(report.IsAvailable);
        Assert.Equal(SiteCommissionUnavailableReason.PermissionNotGranted, report.UnavailableReason);
        Assert.Empty(report.Rows);
    }

    /// <summary>
    /// The regression guard: a fleet that answers is unchanged - available, with its rows, and with
    /// no reason to explain. A business whose Nayax connection works sees no behaviour change.
    /// </summary>
    [Fact]
    public async Task A_fleet_that_answers_is_available_with_no_reason_to_report()
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = 1, MachineName = "Machine A", CustomerID = 50 }]);

        var report = await Handle(fleet, agreement: new CommissionAgreement(
            Id: 1, SiteId: 50, EffectiveFrom: From.AddYears(-1), EffectiveTo: null, CommissionRate: 0.1m,
            Frequency: CommissionFrequency.Monthly, Basis: CommissionBasis.GrossSales,
            PaymentDueDaysAfterPeriodEnd: 14, CreatedAt: From.AddYears(-1), UpdatedAt: From.AddYears(-1)),
            sale: new SiteCommissionSaleFact(1, From.AddDays(1), 100m, "Credit Card", "Water"));

        Assert.True(report.IsAvailable);
        Assert.Null(report.UnavailableReason);
        var row = Assert.Single(report.Rows);
        Assert.Equal(50, row.SiteId);
        Assert.Equal(100m, row.GrossSales);
        Assert.Equal(10m, row.CommissionDue);
    }

    /// <summary>
    /// A business whose fleet read succeeds but holds no site-mapped machine is still an
    /// <em>available</em> answer with no rows: it genuinely has no site to owe anything. That is a
    /// different answer from the unavailable one above, and the two must not collapse into each
    /// other.
    /// </summary>
    [Fact]
    public async Task An_empty_fleet_is_available_with_no_rows_not_unavailable()
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var report = await Handle(fleet);

        Assert.True(report.IsAvailable);
        Assert.Null(report.UnavailableReason);
        Assert.Empty(report.Rows);
    }

    /// <summary>
    /// A support session resolves no Nayax credentials at all (issue #551), and that refusal is not
    /// one of the two availability answers: it propagates, so a reader from outside the business is
    /// told the view is unavailable rather than shown a business with no commission.
    /// </summary>
    [Fact]
    public async Task The_support_session_refusal_propagates_rather_than_reporting_unavailable()
    {
        var fleet = FleetRefusing(new SupportSessionNayaxUnavailableException());

        await Assert.ThrowsAsync<SupportSessionNayaxUnavailableException>(() => Handle(fleet));
    }

    /// <summary>An ordinary upstream failure is still a failure, not a business with no sites.</summary>
    [Fact]
    public async Task An_upstream_failure_propagates_rather_than_reporting_unavailable()
    {
        var fleet = FleetRefusing(new InvalidOperationException("Nayax is unreachable."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handle(fleet));
    }

    [Fact]
    public void Every_reason_publishes_a_stable_code_and_an_explanation()
    {
        Assert.Equal("NayaxNotConnected",
            SiteCommissionUnavailableReasons.Code(SiteCommissionUnavailableReason.NayaxNotConnected));
        Assert.Equal("PermissionNotGranted",
            SiteCommissionUnavailableReasons.Code(SiteCommissionUnavailableReason.PermissionNotGranted));
        Assert.Contains("not a measured zero",
            SiteCommissionUnavailableReasons.Describe(SiteCommissionUnavailableReason.NayaxNotConnected));
        Assert.Contains("not a measured zero",
            SiteCommissionUnavailableReasons.Describe(SiteCommissionUnavailableReason.PermissionNotGranted));
    }

    /// <summary>A value cast from an integer is refused rather than published under a guessed code.</summary>
    [Fact]
    public void An_unknown_reason_is_refused_rather_than_published()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiteCommissionUnavailableReasons.Code((SiteCommissionUnavailableReason)99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiteCommissionUnavailableReasons.Describe((SiteCommissionUnavailableReason)99));
    }

    private static Task<SiteCommissionReport> Handle(
        Mock<INayaxLynxClient> fleet,
        CommissionAgreement? agreement = null,
        SiteCommissionSaleFact? sale = null)
    {
        var store = new Mock<ISiteCommissionStore>();
        store.Setup(x => x.GetCompletedSalesAsync(
                It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale is null ? [] : [sale]);
        store.Setup(x => x.GetAgreementsAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agreement is null ? [] : [agreement]);
        store.Setup(x => x.GetPaymentsAsync(
                It.IsAny<long?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var names = new Mock<ISiteNameResolver>();
        names.Setup(x => x.Resolve(It.IsAny<IReadOnlyList<NayaxMachine>>(), It.IsAny<long>())).Returns("Site 50");

        var calendar = new Mock<IBusinessCalendar>();
        calendar.SetupGet(x => x.Today).Returns(To);

        return new GetSiteCommissionReport(fleet.Object, store.Object, names.Object, calendar.Object)
            .Handle(From, To, null, CancellationToken.None);
    }

    private static Mock<INayaxLynxClient> FleetRefusing(Exception refusal)
    {
        var fleet = new Mock<INayaxLynxClient>();
        fleet.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        return fleet;
    }
}
