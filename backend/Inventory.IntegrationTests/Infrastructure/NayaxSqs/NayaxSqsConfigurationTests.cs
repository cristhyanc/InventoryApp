using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.NayaxSqs;

/// <summary>
/// Issue #1103: what the <c>NayaxSqs</c> settings mean, decided once in the composition root.
///
/// <para>Three states, and the difference between the second and the third is the one that matters.
/// <strong>Nothing configured</strong> is not a misconfiguration: the reader does not start, the API
/// starts exactly as it did before, and that is the shipped state, every test host's state, and the
/// hosted testing environment's state. <strong>Everything configured</strong> starts the reader.
/// <strong>Half configured</strong> - a queue URL with no region or no credential - is a deployment
/// that asked for the reader and would otherwise get an API that silently imports no sales, so it
/// fails at startup with the setting named.</para>
/// </summary>
public class NayaxSqsConfigurationTests
{
    private const string QueueUrl =
        "https://sqs.ap-southeast-2.amazonaws.com/000000000000/fictional-queue";

    private const string DeadLetterQueueUrl =
        "https://sqs.ap-southeast-2.amazonaws.com/000000000000/fictional-queue-dlq";

    private const string AccessKeyId = "AKIAFICTIONALKEYID123";
    private const string SecretAccessKey = "fictionalSecretAccessKeyValue";

    [Fact]
    public void No_settings_at_all_leaves_the_reader_switched_off()
    {
        var configuration = NayaxSqsConfiguration.From(new NayaxSqsOptions());

        Assert.False(configuration.IsConfigured);
        Assert.False(configuration.HasDeadLetterQueue);
        Assert.Null(configuration.QueueUrl);
        Assert.Null(configuration.Region);
        Assert.Null(configuration.AccessKeyId);
        Assert.Null(configuration.SecretAccessKey);
    }

    /// <summary>A section that is absent entirely is the same as one that holds nothing.</summary>
    [Fact]
    public void An_absent_section_leaves_the_reader_switched_off()
    {
        Assert.False(NayaxSqsConfiguration.From(null).IsConfigured);
    }

    /// <summary>A blank queue URL is no queue, not an empty one.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_queue_url_leaves_the_reader_switched_off(string queueUrl)
    {
        Assert.False(NayaxSqsConfiguration.From(new NayaxSqsOptions { QueueUrl = queueUrl }).IsConfigured);
    }

    [Fact]
    public void A_complete_section_configures_the_reader()
    {
        var configuration = NayaxSqsConfiguration.From(Complete());

        Assert.True(configuration.IsConfigured);
        Assert.True(configuration.HasDeadLetterQueue);
        Assert.Equal(QueueUrl, configuration.QueueUrl);
        Assert.Equal(DeadLetterQueueUrl, configuration.DeadLetterQueueUrl);
        Assert.Equal("ap-southeast-2", configuration.Region);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.VisibilityTimeout);
    }

    /// <summary>
    /// The dead-letter queue is optional on its own: a deployment that has not created one yet still
    /// reads the main queue, and simply reports no depth.
    /// </summary>
    [Fact]
    public void A_main_queue_without_a_dead_letter_queue_is_still_configured()
    {
        var options = Complete();
        options.DeadLetterQueueUrl = null;

        var configuration = NayaxSqsConfiguration.From(options);

        Assert.True(configuration.IsConfigured);
        Assert.False(configuration.HasDeadLetterQueue);
    }

    /// <summary>
    /// Half a queue is a refusal at startup, with the missing setting named - and nothing else. The
    /// message must be enough to fix the deployment and must carry no value: a URL, a key id and a
    /// secret are all things that have no business in a log line.
    /// </summary>
    [Theory]
    [InlineData(nameof(NayaxSqsOptions.Region))]
    [InlineData(nameof(NayaxSqsOptions.AccessKeyId))]
    [InlineData(nameof(NayaxSqsOptions.SecretAccessKey))]
    public void A_queue_url_without_the_region_or_the_credential_pair_fails_at_startup(string missing)
    {
        var options = Complete();
        switch (missing)
        {
            case nameof(NayaxSqsOptions.Region):
                options.Region = null;
                break;
            case nameof(NayaxSqsOptions.AccessKeyId):
                options.AccessKeyId = null;
                break;
            default:
                options.SecretAccessKey = null;
                break;
        }

        var refusal = Assert.Throws<InvalidOperationException>(
            () => NayaxSqsConfiguration.From(options));

        Assert.Contains($"NayaxSqs:{missing}", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QueueUrl, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKeyId, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAccessKey, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The visibility timeout is clamped by the Domain's own rule, so a configured value that SQS
    /// would refuse - or that a ten-message batch could not be worked through - cannot reach the
    /// queue.
    /// </summary>
    [Theory]
    [InlineData(0, NayaxSqsQueuePolicy.DefaultVisibilityTimeoutSeconds)]
    [InlineData(5, NayaxSqsQueuePolicy.MinVisibilityTimeoutSeconds)]
    [InlineData(120, 120)]
    [InlineData(999999, NayaxSqsQueuePolicy.MaxVisibilityTimeoutSeconds)]
    public void The_visibility_timeout_is_clamped_into_the_usable_range(
        int configuredSeconds, int expectedSeconds)
    {
        var options = Complete();
        options.VisibilityTimeoutSeconds = configuredSeconds;

        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            NayaxSqsConfiguration.From(options).VisibilityTimeout);
    }

    /// <summary>
    /// A deployment with no queue still gets a client, and it refuses every operation rather than
    /// answering "no messages" - which would make an unconfigured deployment look like a working one
    /// with a quiet queue. The reader checks the settings and never reaches it.
    /// </summary>
    [Fact]
    public async Task The_unconfigured_queue_client_refuses_every_operation()
    {
        var client = new UnconfiguredNayaxSqsQueueClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ReceiveAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChangeVisibilityAsync("handle", TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeleteAsync("handle", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetDeadLetterDepthAsync(CancellationToken.None));
    }

    /// <summary>
    /// And the AWS client refuses to be built at all without a queue, so "the reader was registered
    /// without being configured" cannot become a client pointed at nothing.
    /// </summary>
    [Fact]
    public void The_aws_queue_client_cannot_be_built_without_a_queue()
    {
        var unconfigured = NayaxSqsConfiguration.From(new NayaxSqsOptions());

        Assert.Throws<InvalidOperationException>(() => new AwsNayaxSqsQueueClient(unconfigured));
    }

    private static NayaxSqsOptions Complete() => new()
    {
        QueueUrl = QueueUrl,
        DeadLetterQueueUrl = DeadLetterQueueUrl,
        Region = "ap-southeast-2",
        AccessKeyId = AccessKeyId,
        SecretAccessKey = SecretAccessKey,
    };
}
