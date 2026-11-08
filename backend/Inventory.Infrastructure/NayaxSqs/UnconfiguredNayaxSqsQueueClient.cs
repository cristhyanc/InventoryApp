using Inventory.Application.NayaxSqs;

namespace Inventory.Infrastructure.NayaxSqs;

/// <summary>
/// The <see cref="INayaxSqsQueueClient"/> an environment with no queue configured gets (issue #1103),
/// on exactly the terms <c>UnconfiguredNayaxTokenProtector</c> stands in for missing key material.
///
/// <para>With no <c>NayaxSqs:QueueUrl</c> there is no queue, no region and no credential, so there is
/// nothing to build an AWS client from - and the API still has to start normally, which every test
/// host, the hosted testing environment and the shipped state depend on. The reader checks
/// <see cref="INayaxSqsReaderSettings.IsConfigured"/> and returns before touching this, so every
/// method here is unreachable; each one refuses rather than answering, because a client that returned
/// "no messages" would make an unconfigured deployment look like a working one with a quiet
/// queue.</para>
/// </summary>
public sealed class UnconfiguredNayaxSqsQueueClient : INayaxSqsQueueClient
{
    private const string Unconfigured =
        "No Nayax SQS queue is configured, so the sales reader performs no queue operation. "
        + "Provision NayaxSqs:QueueUrl to switch it on.";

    /// <inheritdoc />
    public Task<IReadOnlyList<NayaxSqsReceivedMessage>> ReceiveAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Unconfigured);

    /// <inheritdoc />
    public Task ChangeVisibilityAsync(
        string receiptHandle, TimeSpan visibility, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Unconfigured);

    /// <inheritdoc />
    public Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Unconfigured);

    /// <inheritdoc />
    public Task<int?> GetDeadLetterDepthAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Unconfigured);
}
