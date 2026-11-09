using Inventory.Application.Imports;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// The real waiting behind <see cref="IReimbursementImportJobSchedule"/> (issue #1149):
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and nothing else.
///
/// <para>It is an adapter for the reason <c>SystemNayaxSalesReconciliationSchedule</c> is one - so
/// the worker's start-up delay and idle cadence can be asserted without a test actually waiting -
/// and it is deliberately this small: every decision about <em>how long</em> belongs to
/// <see cref="ReimbursementImportJobRunner"/>.</para>
/// </summary>
public sealed class SystemReimbursementImportJobSchedule : IReimbursementImportJobSchedule
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
}
