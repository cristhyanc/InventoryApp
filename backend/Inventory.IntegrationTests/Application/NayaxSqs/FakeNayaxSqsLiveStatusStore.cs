using Inventory.Application.NayaxSqs;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// The stored half of the live decision of issue #1106, as a test double: what the current business's
/// Nayax SQS sender row says, with no database behind it.
///
/// <para>The default is the state of a business nobody has recorded a sender for, which is the state
/// every test that is not about this issue is about - and the state in which the Dashboard behaves
/// exactly as it did before. The named factories below are the states the issue's own cases need.</para>
/// </summary>
internal sealed class FakeNayaxSqsLiveStatusStore : INayaxSqsLiveStatusStore
{
    private readonly NayaxSqsLiveSenderStatus? _status;

    public FakeNayaxSqsLiveStatusStore(NayaxSqsLiveSenderStatus? status = null)
    {
        _status = status;
    }

    /// <summary>How many times the use case has read the row - used to prove it reads it once.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// A business with an enabled sender whose reconciliation last fully succeeded at
    /// <paramref name="reconciliationSucceededAtUtc"/>, and whose queue last delivered at
    /// <paramref name="lastMessageReceivedAtUtc"/> - <see langword="null"/> for a queue that has
    /// delivered nothing yet, which is a quiet queue and not a failure.
    /// </summary>
    public static FakeNayaxSqsLiveStatusStore Enabled(
        DateTime? reconciliationSucceededAtUtc,
        DateTime? lastMessageReceivedAtUtc = null) =>
        new(new NayaxSqsLiveSenderStatus(
            SenderIdRecorded: true,
            IsEnabled: true,
            lastMessageReceivedAtUtc,
            reconciliationSucceededAtUtc));

    /// <summary>A business whose recorded sender is switched off.</summary>
    public static FakeNayaxSqsLiveStatusStore Disabled(DateTime? reconciliationSucceededAtUtc) =>
        new(new NayaxSqsLiveSenderStatus(
            SenderIdRecorded: true,
            IsEnabled: false,
            LastMessageReceivedAtUtc: null,
            reconciliationSucceededAtUtc));

    /// <inheritdoc />
    public Task<NayaxSqsLiveSenderStatus?> FindCurrentAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(_status);
    }
}
