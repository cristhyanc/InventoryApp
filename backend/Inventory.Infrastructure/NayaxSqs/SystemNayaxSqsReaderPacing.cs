using Inventory.Application.NayaxSqs;

namespace Inventory.Infrastructure.NayaxSqs;

/// <summary>
/// The real waiting and the real jitter behind <see cref="INayaxSqsReaderPacing"/> (issue #1103):
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and <see cref="Random.Shared"/>, and nothing
/// else.
///
/// <para>It is an adapter for the reason the clock is one - so the reader's bounds can be asserted
/// without a test actually waiting a minute - and it is deliberately this small: every decision about
/// <em>how long</em> belongs to <c>Inventory.Domain.NayaxSqs.NayaxSqsQueuePolicy</c>.</para>
/// </summary>
public sealed class SystemNayaxSqsReaderPacing : INayaxSqsReaderPacing
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(duration, cancellationToken);

    /// <inheritdoc />
    public double NextJitterSample() => Random.Shared.NextDouble();
}
