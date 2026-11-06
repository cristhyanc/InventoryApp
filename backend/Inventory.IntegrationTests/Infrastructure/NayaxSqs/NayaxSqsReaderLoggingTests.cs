using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.NayaxSqs;
using InventoryApi.Tests.Application.NayaxSqs;
using InventoryApi.Tests.Application.Time;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.NayaxSqs;

/// <summary>
/// Issue #1103's required log-capture case: whatever the Nayax SQS sales reader says about a message,
/// <strong>no message body, receipt handle, card or customer value, AWS key or queue URL reaches a
/// log sink</strong> (AGENTS.md § Nayax integration).
///
/// <para>The reader is driven over the <em>real</em> <see cref="LoggingNayaxSqsReaderLog"/> and a
/// capturing <c>ILogger</c>, with the message that carries the shape of the first real transaction
/// Nayax delivered - card string, display number, cardholder name, consumer id, merchant and terminal
/// ids and processor references, every value fictional (<see cref="NayaxSqsRealMessage"/>). Every
/// outcome the reader can reach is driven: a saved sale, a refused one, a failed extension, a failed
/// delete, a failed poll and a dead-letter warning. Then every captured line is checked against every
/// one of those values at once.</para>
///
/// <para>What makes the property structural rather than a matter of care is the port:
/// <see cref="INayaxSqsReaderLog"/> has no method with a parameter that <em>could</em> carry one of
/// them, which the last case here asserts over its own signatures. The capture is the belt; the
/// signatures are the braces.</para>
/// </summary>
public class NayaxSqsReaderLoggingTests
{
    private const string Sender = "AIDAFICTIONALSENDER01";
    private const string ReceiptHandle =
        "AQEBfictionalreceipthandlethatmustneverbeloggedanywhere0123456789";
    private const string AccessKeyId = "AKIAFICTIONALKEYID123";
    private const string SecretAccessKey = "fictionalSecretAccessKeyThatMustNeverBeLogged";
    private const string QueueUrl = "https://sqs.ap-southeast-2.amazonaws.com/000000000000/fictional-queue";

    private static readonly DateTime Now = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Every reader outcome, over the real log adapter, against a message full of card and customer
    /// values. Nothing it said carries any of them, nor the body, nor the receipt handle.
    /// </summary>
    [Fact]
    public async Task No_reader_log_line_carries_a_body_a_handle_a_card_value_or_a_key()
    {
        var logger = new CapturingLogger<LoggingNayaxSqsReaderLog>();
        var log = new LoggingNayaxSqsReaderLog(logger);

        var queue = new FakeNayaxSqsQueueClient();
        var scopes = new FakeNayaxSqsReaderScopes().WithSender(Sender, businessId: 7);
        var settings = new FakeNayaxSqsReaderSettings();
        var reader = new NayaxSqsSalesReader(
            queue, scopes, settings, new FakeNayaxSqsReaderPacing(), new NayaxSqsReaderHealth(), log,
            new FakeClock(Now));

        // A dead-letter queue with messages in it, so the warning is emitted too.
        queue.DeadLetterDepth = 3;

        // A saved sale, from the sender that is recorded.
        queue.Returns(Message("m1"));
        await reader.PollOnceAsync(CancellationToken.None);

        // A message nobody may own, which is the outcome whose reason code is most tempting to
        // explain with a payload.
        queue.Returns(Message("m2", senderId: "AIDAUNKNOWNSENDER0001"));
        await reader.PollOnceAsync(CancellationToken.None);

        // An extension that failed, so the message is skipped.
        queue.VisibilityFailures.Add(ReceiptHandle);
        queue.Returns(Message("m3"));
        await reader.PollOnceAsync(CancellationToken.None);
        queue.VisibilityFailures.Clear();

        // A delete that failed after the sale was durably stored.
        queue.DeleteFailures.Add(ReceiptHandle);
        queue.Returns(Message("m4"));
        await reader.PollOnceAsync(CancellationToken.None);
        queue.DeleteFailures.Clear();

        // A receive that failed, and the backoff that follows it.
        queue.Fails(new InvalidOperationException(
            $"The request to {QueueUrl} signed with {AccessKeyId} failed."));
        var failed = await reader.PollOnceAsync(CancellationToken.None);
        log.PollFailed(
            nameof(InvalidOperationException),
            failed.ConsecutivePollFailures,
            NayaxSqsQueuePolicy.PollBackoff(failed.ConsecutivePollFailures, 0.5));

        // And the two lifecycle lines, which is every method on the port.
        log.ReaderNotConfigured();
        log.ReaderStarted();
        log.ReaderStopped();

        var captured = string.Join("\n", logger.Messages);

        Assert.NotEmpty(logger.Messages);

        // The body itself, and every card, consumer, merchant, terminal and processor value in it.
        Assert.DoesNotContain(NayaxSqsRealMessage.Received, captured, StringComparison.Ordinal);
        foreach (var value in NayaxSqsRealMessage.CardAndCustomerValues)
        {
            Assert.DoesNotContain(value, captured, StringComparison.Ordinal);
        }

        // The receipt handle, which is a capability to change or delete a queue message.
        Assert.DoesNotContain(ReceiptHandle, captured, StringComparison.Ordinal);

        // The AWS credential pair, the queue URL, and the sender id - which is an identifier of
        // somebody else's IAM user and is on no line either.
        Assert.DoesNotContain(AccessKeyId, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAccessKey, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(QueueUrl, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(Sender, captured, StringComparison.Ordinal);

        // And it did say something useful: the queue message id and the fixed reason codes.
        Assert.Contains("m1", captured, StringComparison.Ordinal);
        Assert.Contains(nameof(NayaxSqsProcessingReason.SenderUnknown), captured, StringComparison.Ordinal);
    }

    /// <summary>
    /// The structural half. No method on the reader's log port has a parameter that could carry a
    /// payload: every one of them takes a queue message id, a business id, a count, a duration, a
    /// fixed enum code or an exception <em>type name</em>. In particular nothing takes an
    /// <see cref="Exception"/>, whose own message can quote what it failed on.
    /// </summary>
    [Fact]
    public void No_method_on_the_reader_log_port_can_accept_an_exception_or_a_payload()
    {
        var parameters = typeof(INayaxSqsReaderLog)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .ToArray();

        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(typeof(Exception), parameters.Select(parameter => parameter.ParameterType));

        // Every parameter is one of the five shapes a reader line may hold.
        Assert.All(parameters, parameter =>
            Assert.True(
                parameter.ParameterType == typeof(string)
                || parameter.ParameterType == typeof(int)
                || parameter.ParameterType == typeof(int?)
                || parameter.ParameterType == typeof(TimeSpan)
                || parameter.ParameterType.IsEnum,
                $"{parameter.Member.Name}({parameter.Name}) is a {parameter.ParameterType.Name}, "
                    + "which is not one of the shapes a Nayax SQS reader log line may carry."));

        // And the only string parameters are the message id and the failure type name, never a body,
        // a handle, a sender id or a URL.
        Assert.All(
            parameters.Where(parameter => parameter.ParameterType == typeof(string)),
            parameter => Assert.Contains(
                parameter.Name,
                (string[])["messageId", "failureType"],
                StringComparer.Ordinal));
    }

    private static NayaxSqsReceivedMessage Message(string messageId, string? senderId = Sender) =>
        new(messageId, ReceiptHandle, NayaxSqsRealMessage.Received, senderId, 1);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
