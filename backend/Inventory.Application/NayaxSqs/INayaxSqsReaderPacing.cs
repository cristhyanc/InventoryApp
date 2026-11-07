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
    /// Waits until <paramref name="duration"/> has passed, until <paramref name="inFlight"/> has
    /// finished, or until <paramref name="cancellationToken"/> is cancelled - whichever happens first.
    ///
    /// <para>This is how the reader renews the visibility of a message that is <em>still being
    /// processed</em>, and it is a method of its own rather than <see cref="DelayAsync"/> because
    /// "whichever comes first" is the whole behaviour: a wait that ignored the work would keep
    /// renewing a message that finished a minute ago, and one that ignored the duration would renew
    /// nothing. The work is passed in rather than polled so that a wait which ends with it ends
    /// <em>knowing</em> it has, which is what lets a case assert how many renewals one slow message
    /// caused instead of racing the answer.</para>
    ///
    /// <para>It never reports what the work ended with: the caller owns that task and awaits it
    /// itself. A non-positive duration returns immediately.</para>
    /// </summary>
    Task WaitForRenewalAsync(TimeSpan duration, Task inFlight, CancellationToken cancellationToken);

    /// <summary>
    /// A fresh sample in <c>[0,1)</c> for
    /// <c>NayaxSqsQueuePolicy.PollBackoff</c>'s jitter, so two processes - or one process across two
    /// outages - do not retry in lockstep.
    /// </summary>
    double NextJitterSample();
}
