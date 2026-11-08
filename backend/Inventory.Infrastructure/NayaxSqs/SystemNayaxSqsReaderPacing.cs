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
    public async Task WaitForRenewalAsync(
        TimeSpan duration, Task inFlight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inFlight);

        if (duration <= TimeSpan.Zero || inFlight.IsCompleted)
        {
            return;
        }

        // Linked and cancelled as soon as the wait is over, so the timer of a message that finished
        // after a second is released then rather than held for the rest of the renewal interval - a
        // batch of ten messages would otherwise leave ten of them pending at once.
        using var over = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var waiting = Task.Delay(duration, over.Token);

        await Task.WhenAny(inFlight, waiting).ConfigureAwait(false);

        await over.CancelAsync().ConfigureAwait(false);

        // Neither task is awaited here: the delay's own cancellation is this method's doing and says
        // nothing, and what the work ended with belongs to the caller that owns it. The cancellation
        // the caller passed in is answered by it too, on the task it awaits.
    }

    /// <inheritdoc />
    public double NextJitterSample() => Random.Shared.NextDouble();
}
