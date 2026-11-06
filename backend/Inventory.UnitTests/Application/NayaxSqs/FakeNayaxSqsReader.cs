using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// The doubles the Nayax SQS sales reader's loop is driven by (issue #1103). They are deliberately
/// recording rather than asserting: what the reader did to a queue - which handle it deleted with,
/// what visibility it set, in which order - is the behaviour under test, so a case reads the record
/// instead of a mock's expectation script.
/// </summary>
internal sealed class FakeNayaxSqsQueueClient : INayaxSqsQueueClient
{
    private readonly Queue<Func<IReadOnlyList<NayaxSqsReceivedMessage>>> _batches = new();

    /// <summary>Every <c>ChangeMessageVisibility</c> made, in order.</summary>
    public List<(string ReceiptHandle, TimeSpan Visibility)> VisibilityChanges { get; } = [];

    /// <summary>Every <c>DeleteMessage</c> made, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>How many times <c>ReceiveMessage</c> was called.</summary>
    public int Receives { get; private set; }

    /// <summary>How many times the dead-letter queue's depth was read.</summary>
    public int DeadLetterDepthReads { get; private set; }

    /// <summary>The depth the dead-letter queue reports, or <see langword="null"/> for "no queue".</summary>
    public int? DeadLetterDepth { get; set; }

    /// <summary>Set to make the dead-letter depth read fail.</summary>
    public Exception? DeadLetterDepthFailure { get; set; }

    /// <summary>
    /// Receipt handles whose <c>ChangeMessageVisibility</c> fails, which is how "another receiver may
    /// now hold this message" is expressed.
    /// </summary>
    public HashSet<string> VisibilityFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>Receipt handles whose <c>DeleteMessage</c> fails.</summary>
    public HashSet<string> DeleteFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>Queues one batch for the next receive.</summary>
    public FakeNayaxSqsQueueClient Returns(params NayaxSqsReceivedMessage[] messages)
    {
        _batches.Enqueue(() => messages);
        return this;
    }

    /// <summary>Queues a receive that fails.</summary>
    public FakeNayaxSqsQueueClient Fails(Exception failure)
    {
        _batches.Enqueue(() => throw failure);
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NayaxSqsReceivedMessage>> ReceiveAsync(CancellationToken cancellationToken)
    {
        Receives++;
        cancellationToken.ThrowIfCancellationRequested();

        // An exhausted script answers an empty batch rather than throwing: a loop test ends by
        // cancelling, and an empty queue is the ordinary quiet state anyway.
        return Task.FromResult(_batches.Count > 0 ? _batches.Dequeue()() : []);
    }

    /// <inheritdoc />
    public Task ChangeVisibilityAsync(
        string receiptHandle, TimeSpan visibility, CancellationToken cancellationToken)
    {
        if (VisibilityFailures.Contains(receiptHandle))
        {
            return Task.FromException(new InvalidOperationException("Receipt handle is no longer valid."));
        }

        VisibilityChanges.Add((receiptHandle, visibility));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        if (DeleteFailures.Contains(receiptHandle))
        {
            return Task.FromException(new InvalidOperationException("Receipt handle is no longer valid."));
        }

        Deleted.Add(receiptHandle);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int?> GetDeadLetterDepthAsync(CancellationToken cancellationToken)
    {
        DeadLetterDepthReads++;

        return DeadLetterDepthFailure is { } failure
            ? Task.FromException<int?>(failure)
            : Task.FromResult(DeadLetterDepth);
    }
}

/// <summary>
/// The reader's two scopes, scripted: which business a sender id belongs to, and what processing one
/// message in that business answered.
/// </summary>
internal sealed class FakeNayaxSqsReaderScopes : INayaxSqsReaderScopes
{
    private readonly Dictionary<string, NayaxSqsSenderAttribution> _senders = new(StringComparer.Ordinal);

    /// <summary>Every message id processed, in order, with the business it was processed in.</summary>
    public List<(int BusinessId, string MessageId)> Processed { get; } = [];

    /// <summary>How many per-business scopes were opened, and how many were disposed.</summary>
    public int ScopesOpened { get; private set; }

    /// <summary>How many of those scopes were disposed. A leak would show as a difference.</summary>
    public int ScopesDisposed { get; private set; }

    /// <summary>What processing answers. Replace it to script a per-message outcome.</summary>
    public Func<string, NayaxSqsProcessingOutcome> Outcome { get; set; } =
        _ => NayaxSqsProcessingOutcome.Done(NayaxSqsProcessingReason.Saved);

    /// <summary>Set to make the sender lookup itself fail.</summary>
    public Exception? LookupFailure { get; set; }

    /// <summary>Records <paramref name="senderId"/> as an enabled sender of an active business.</summary>
    public FakeNayaxSqsReaderScopes WithSender(
        string senderId,
        int businessId,
        bool isEnabled = true,
        BusinessStatus status = BusinessStatus.Active)
    {
        _senders[senderId] = new NayaxSqsSenderAttribution(businessId, isEnabled, status);
        return this;
    }

    /// <inheritdoc />
    public Task<NayaxSqsSenderAttribution?> FindBusinessBySenderAsync(
        string senderId, CancellationToken cancellationToken) =>
        LookupFailure is { } failure
            ? Task.FromException<NayaxSqsSenderAttribution?>(failure)
            : Task.FromResult(_senders.TryGetValue(senderId, out var attribution) ? attribution : null);

    /// <inheritdoc />
    public INayaxSqsBusinessWorkScope CreateForBusiness(int businessId)
    {
        ScopesOpened++;
        return new WorkScope(this, businessId);
    }

    private sealed class WorkScope : INayaxSqsBusinessWorkScope
    {
        private readonly FakeNayaxSqsReaderScopes _owner;
        private readonly int _businessId;

        public WorkScope(FakeNayaxSqsReaderScopes owner, int businessId)
        {
            _owner = owner;
            _businessId = businessId;
        }

        public Task<NayaxSqsProcessingOutcome> ProcessAsync(
            string messageId, string? messageBody, CancellationToken cancellationToken)
        {
            _owner.Processed.Add((_businessId, messageId));
            return Task.FromResult(_owner.Outcome(messageId));
        }

        public ValueTask DisposeAsync()
        {
            _owner.ScopesDisposed++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The reader's log, captured. It records the fixed codes rather than formatted sentences, which is
/// the point of the port: a case can assert what was said without a sink in the way.
/// </summary>
internal sealed class FakeNayaxSqsReaderLog : INayaxSqsReaderLog
{
    public int NotConfiguredLines { get; private set; }

    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public List<(string FailureType, int ConsecutiveFailures, TimeSpan Backoff)> PollFailures { get; } = [];

    public List<(string MessageId, int? BusinessId, NayaxSqsDisposition Disposition, NayaxSqsProcessingReason Reason)>
        Handled { get; } = [];

    public List<string> ExtensionFailures { get; } = [];

    public List<string> VisibilityChangeFailures { get; } = [];

    public List<string> DeleteFailures { get; } = [];

    public List<int> DeadLetterWarnings { get; } = [];

    public List<string> DeadLetterReadFailures { get; } = [];

    public void ReaderNotConfigured() => NotConfiguredLines++;

    public void ReaderStarted() => Started++;

    public void ReaderStopped() => Stopped++;

    public void PollFailed(string failureType, int consecutivePollFailures, TimeSpan backoff) =>
        PollFailures.Add((failureType, consecutivePollFailures, backoff));

    public void MessageHandled(
        string messageId,
        int? businessId,
        NayaxSqsDisposition disposition,
        NayaxSqsProcessingReason reason) =>
        Handled.Add((messageId, businessId, disposition, reason));

    public void VisibilityExtensionFailed(string messageId, string failureType) =>
        ExtensionFailures.Add(messageId);

    public void VisibilityChangeFailed(string messageId, string failureType) =>
        VisibilityChangeFailures.Add(messageId);

    public void DeleteFailed(string messageId, string failureType) => DeleteFailures.Add(messageId);

    public void DeadLetterQueueNotEmpty(int depth) => DeadLetterWarnings.Add(depth);

    public void DeadLetterDepthReadFailed(string failureType) => DeadLetterReadFailures.Add(failureType);
}

/// <summary>
/// Waiting and jitter with the waiting taken out: every delay is recorded and returns at once, so a
/// case can assert the bound the reader chose without spending it.
/// </summary>
internal sealed class FakeNayaxSqsReaderPacing : INayaxSqsReaderPacing
{
    /// <summary>Every delay asked for, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>The jitter sample handed to the policy. Mid-range unless a case says otherwise.</summary>
    public double JitterSample { get; set; } = 0.5;

    /// <summary>
    /// A hook run on each delay, so a loop case can stop the reader from inside the wait it would
    /// otherwise have spent - which is how "it backed off and kept going" is asserted without
    /// actually waiting a minute.
    /// </summary>
    public Action<TimeSpan>? OnDelay { get; set; }

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Delays.Add(duration);
        OnDelay?.Invoke(duration);
        return Task.CompletedTask;
    }

    public double NextJitterSample() => JitterSample;
}

/// <summary>The reader's settings, stated outright.</summary>
internal sealed class FakeNayaxSqsReaderSettings : INayaxSqsReaderSettings
{
    public bool IsConfigured { get; set; } = true;

    public bool HasDeadLetterQueue { get; set; } = true;

    public TimeSpan VisibilityTimeout { get; set; } =
        NayaxSqsQueuePolicy.VisibilityTimeout(NayaxSqsQueuePolicy.DefaultVisibilityTimeoutSeconds);
}
