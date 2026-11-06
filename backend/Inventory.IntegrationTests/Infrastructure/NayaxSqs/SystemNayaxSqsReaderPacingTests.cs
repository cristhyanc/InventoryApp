using Inventory.Infrastructure.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.NayaxSqs;

/// <summary>
/// Issue #1103: the real waiting behind <c>INayaxSqsReaderPacing</c>, and in particular the
/// renewal wait the reader keeps a message's visibility window open with.
///
/// <para>Its whole behaviour is "whichever comes first", and both halves matter: a wait that ignored
/// the in-flight work would keep renewing a message that finished a minute ago, and one that ignored
/// the duration would renew nothing at all - which is the defect that made a message taking longer
/// than its visibility timeout expose itself and every message waiting behind it. The durations here
/// are deliberately tiny, because what is asserted is which of the two ended the wait, not how long
/// either is.</para>
/// </summary>
public class SystemNayaxSqsReaderPacingTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_renewal_wait_ends_when_the_message_finishes_before_the_interval_is_up()
    {
        var pacing = new SystemNayaxSqsReaderPacing();
        var inFlight = new TaskCompletionSource();

        var waiting = pacing.WaitForRenewalAsync(Long, inFlight.Task, CancellationToken.None);

        Assert.False(waiting.IsCompleted);

        inFlight.SetResult();

        // It returns with the message rather than after the interval, which is what keeps the reader
        // from renewing a message it has already finished with.
        await waiting;
    }

    [Fact]
    public async Task A_renewal_wait_ends_when_the_interval_is_up_while_the_message_is_still_running()
    {
        var pacing = new SystemNayaxSqsReaderPacing();
        var stillRunning = new TaskCompletionSource();

        await pacing.WaitForRenewalAsync(
            TimeSpan.FromMilliseconds(1), stillRunning.Task, CancellationToken.None);

        // The message is deliberately left in flight: the wait came back for the renewal, which is
        // the half that renews the batch of a message that outlasts its window.
        Assert.False(stillRunning.Task.IsCompleted);

        stillRunning.SetResult();
    }

    [Fact]
    public async Task A_renewal_wait_ends_on_cancellation_without_waiting_out_the_interval()
    {
        var pacing = new SystemNayaxSqsReaderPacing();
        var stillRunning = new TaskCompletionSource();

        using var shuttingDown = new CancellationTokenSource();

        var waiting = pacing.WaitForRenewalAsync(Long, stillRunning.Task, shuttingDown.Token);

        await shuttingDown.CancelAsync();

        // Shutdown ends the wait and is not reported here: what the in-flight message ends with
        // belongs to the reader, which awaits that task itself.
        await waiting;

        stillRunning.SetResult();
    }

    [Fact]
    public async Task A_renewal_wait_for_nothing_returns_at_once()
    {
        var pacing = new SystemNayaxSqsReaderPacing();

        // A non-positive interval and an already finished message are both "there is nothing to wait
        // for", and neither starts a timer.
        await pacing.WaitForRenewalAsync(TimeSpan.Zero, Task.CompletedTask, CancellationToken.None);
        await pacing.WaitForRenewalAsync(Long, Task.CompletedTask, CancellationToken.None);
    }
}
