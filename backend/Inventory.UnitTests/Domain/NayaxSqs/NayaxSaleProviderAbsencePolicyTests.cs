using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// What it means that <c>lastSales</c> was read for a sale's machine and did not return that sale
/// (issue #1104).
///
/// <para><strong>Absence is never proof.</strong> Nayax documents <c>lastSales</c> as "the most
/// recent sales" for a machine: it does not say how many, and it promises no complete
/// authorization-time window. A transaction missing from the response is therefore not evidence that
/// the transaction did not happen, and nothing in this policy - or anywhere that reads what it
/// records - may treat it as such. What it answers is narrower and checkable: was the sale's own
/// instant strictly inside the range the response did return, and had the sale been in hand long
/// enough that the provider had a chance to know about it?</para>
///
/// <para>Only when both are true is an observation worth recording at all, and even then it says
/// "look at this", never "this sale is fake".</para>
/// </summary>
public class NayaxSaleProviderAbsencePolicyTests
{
    private static readonly DateTime RunStarted = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Earliest = RunStarted.AddHours(-6);
    private static readonly DateTime Latest = RunStarted.AddMinutes(-30);

    /// <summary>
    /// The one case that records anything: a sale the response bracketed and that had been in hand
    /// for longer than the grace. It is flagged for a person, and nothing more.
    /// </summary>
    [Fact]
    public void A_bracketed_sale_outside_the_receipt_grace_is_recorded_and_needs_investigation()
    {
        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            Window(),
            saleAuthorizationInstantUtc: RunStarted.AddHours(-3),
            saleReceivedViaSqsAtUtc: RunStarted.AddHours(-3),
            runStartedAtUtc: RunStarted);

        Assert.True(observation.Bracketed);
        Assert.True(observation.OutsideReceiptGrace);
        Assert.True(observation.RecordsEvidence);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, observation.ObservedState);
    }

    /// <summary>
    /// Outside the returned range the response says nothing at all: a sale older than the oldest item
    /// returned is simply past the end of a rolling window, and a sale newer than the newest is one
    /// the window has not caught up with.
    /// </summary>
    [Theory]
    [InlineData(-7, 0)]
    [InlineData(0, -10)]
    public void A_sale_that_is_not_bracketed_records_nothing_and_changes_nothing(
        int instantHoursFromRun, int instantMinutesFromRun)
    {
        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            Window(),
            saleAuthorizationInstantUtc: RunStarted
                .AddHours(instantHoursFromRun)
                .AddMinutes(instantMinutesFromRun),
            saleReceivedViaSqsAtUtc: RunStarted.AddHours(-8),
            runStartedAtUtc: RunStarted);

        Assert.False(observation.Bracketed);
        Assert.False(observation.RecordsEvidence);
        Assert.Null(observation.ObservedState);
    }

    /// <summary>
    /// "Strictly between", so a sale at either end of the returned range is not bracketed by it: the
    /// boundary items are what the window reached, not evidence about anything inside it.
    /// </summary>
    [Fact]
    public void A_sale_exactly_at_a_boundary_of_the_returned_range_is_not_bracketed()
    {
        foreach (var boundary in new[] { Earliest, Latest })
        {
            var observation = NayaxSaleProviderAbsencePolicy.Observe(
                Window(),
                saleAuthorizationInstantUtc: boundary,
                saleReceivedViaSqsAtUtc: RunStarted.AddHours(-8),
                runStartedAtUtc: RunStarted);

            Assert.False(observation.Bracketed);
            Assert.False(observation.RecordsEvidence);
        }
    }

    /// <summary>
    /// A sale the queue handed over moments ago is one the provider's own record may simply not show
    /// yet, so it is left alone. The grace is ten minutes before the run started.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-10)]
    public void A_sale_received_inside_the_receipt_grace_records_nothing(int receivedMinutesFromRun)
    {
        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            Window(),
            saleAuthorizationInstantUtc: RunStarted.AddHours(-3),
            saleReceivedViaSqsAtUtc: RunStarted.AddMinutes(receivedMinutesFromRun),
            runStartedAtUtc: RunStarted);

        Assert.True(observation.Bracketed);
        Assert.False(observation.OutsideReceiptGrace);
        Assert.False(observation.RecordsEvidence);
        Assert.Null(observation.ObservedState);
    }

    [Fact]
    public void A_sale_received_just_past_the_grace_is_outside_it()
    {
        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            Window(),
            saleAuthorizationInstantUtc: RunStarted.AddHours(-3),
            saleReceivedViaSqsAtUtc: RunStarted
                - NayaxSaleProviderAbsencePolicy.ReceiptGrace
                - TimeSpan.FromSeconds(1),
            runStartedAtUtc: RunStarted);

        Assert.True(observation.OutsideReceiptGrace);
        Assert.True(observation.RecordsEvidence);
    }

    /// <summary>
    /// An empty response demonstrates nothing: there is no range for a sale to be inside. A response
    /// with one item is the same - one instant is both ends, so nothing is strictly between them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_response_with_no_range_brackets_nothing(int returnedItems)
    {
        var window = returnedItems == 0
            ? new NayaxProviderMachineWindow(
                MachineId: 42,
                ReturnedItemCount: 0,
                EarliestReturnedInstantUtc: null,
                LatestReturnedInstantUtc: null)
            : new NayaxProviderMachineWindow(
                MachineId: 42,
                ReturnedItemCount: 1,
                EarliestReturnedInstantUtc: Latest,
                LatestReturnedInstantUtc: Latest);

        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            window,
            saleAuthorizationInstantUtc: RunStarted.AddHours(-3),
            saleReceivedViaSqsAtUtc: RunStarted.AddHours(-8),
            runStartedAtUtc: RunStarted);

        Assert.False(observation.Bracketed);
        Assert.False(observation.RecordsEvidence);
    }

    /// <summary>
    /// A sale with no authorization instant of its own cannot be placed inside any range, and a sale
    /// that never came from the queue has no receipt instant to apply the grace to. Neither is
    /// evidence of anything.
    /// </summary>
    [Fact]
    public void A_sale_with_no_instant_or_no_receipt_records_nothing()
    {
        var withoutInstant = NayaxSaleProviderAbsencePolicy.Observe(
            Window(), null, RunStarted.AddHours(-8), RunStarted);
        var withoutReceipt = NayaxSaleProviderAbsencePolicy.Observe(
            Window(), RunStarted.AddHours(-3), null, RunStarted);

        Assert.False(withoutInstant.Bracketed);
        Assert.False(withoutInstant.RecordsEvidence);
        Assert.False(withoutReceipt.OutsideReceiptGrace);
        Assert.False(withoutReceipt.RecordsEvidence);
    }

    /// <summary>
    /// The policy's own answer never includes "the provider's window is complete": it has three
    /// observations and a state, and none of them is a completeness claim. This is the structural
    /// half of the issue's rule that absence is never proof - there is no property to misread.
    /// </summary>
    [Fact]
    public void The_observation_publishes_no_completeness_claim()
    {
        var names = typeof(NayaxSaleAbsenceObservation)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(
            new[] { "Bracketed", "ObservedState", "OutsideReceiptGrace", "RecordsEvidence" },
            names.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static NayaxProviderMachineWindow Window() => new(
        MachineId: 42,
        ReturnedItemCount: 10,
        EarliestReturnedInstantUtc: Earliest,
        LatestReturnedInstantUtc: Latest);
}
