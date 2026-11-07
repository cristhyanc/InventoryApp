using Inventory.Application.NayaxReconciliation;

namespace Inventory.Infrastructure.NayaxReconciliation;

/// <summary>
/// The real waiting behind <see cref="INayaxSalesReconciliationSchedule"/> (issue #1104):
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and nothing else.
///
/// <para>It is an adapter for the reason <c>SystemNayaxSqsReaderPacing</c> is one - so the runner's
/// first-sweep delay and hourly cadence can be asserted without a test actually waiting an hour -
/// and it is deliberately this small: every decision about <em>how long</em> belongs to
/// <see cref="NayaxSalesReconciliationRunner"/>.</para>
/// </summary>
public sealed class SystemNayaxSalesReconciliationSchedule : INayaxSalesReconciliationSchedule
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
}
