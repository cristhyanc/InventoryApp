namespace Inventory.Application.NayaxSqs;

/// <summary>
/// The two non-deterministic things the Nayax SQS reader's loop needs - waiting, and spreading a
/// backoff - behind one narrow port (issue #1103).
///
/// <para>They are a port for the reason <c>IClock</c> is one: a loop that called
/// <c>Task.Delay</c> and drew its own random number could not be tested at its boundaries without
/// actually waiting a minute, and "how long did it wait, and was it bounded?" is exactly what has to
/// be asserted. The arithmetic itself is not here - it is
/// <c>Inventory.Domain.NayaxSqs.NayaxSqsQueuePolicy</c>, which this only supplies a sample to.</para>
/// </summary>
public interface INayaxSqsReaderPacing
{
    /// <summary>
    /// Waits <paramref name="duration"/>, or returns as soon as
    /// <paramref name="cancellationToken"/> is cancelled, which is the host shutting down. A
    /// non-positive duration returns immediately.
    /// </summary>
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// A fresh sample in <c>[0,1)</c> for
    /// <c>NayaxSqsQueuePolicy.PollBackoff</c>'s jitter, so two processes - or one process across two
    /// outages - do not retry in lockstep.
    /// </summary>
    double NextJitterSample();
}
