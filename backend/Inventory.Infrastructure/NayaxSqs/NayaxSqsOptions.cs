using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;

namespace Inventory.Infrastructure.NayaxSqs;

/// <summary>
/// The single typed configuration contract for the shared Nayax SQS sales queue (issue #1103), bound
/// once in the composition root instead of the reader reaching into <c>IConfiguration</c>.
///
/// <para><strong>In production every value comes from App Service application settings or a Key Vault
/// reference, and none of them is ever in the repository.</strong> The names are
/// <c>NayaxSqs__QueueUrl</c>, <c>NayaxSqs__DeadLetterQueueUrl</c>, <c>NayaxSqs__Region</c>,
/// <c>NayaxSqs__AccessKeyId</c>, <c>NayaxSqs__SecretAccessKey</c> and
/// <c>NayaxSqs__VisibilityTimeoutSeconds</c>; a human sets them (README.md § Nayax SQS sales reader),
/// and the hosted testing environment is deliberately given none (docs/testing-environment.md).</para>
///
/// <para><strong>With no <see cref="QueueUrl"/> nothing starts and nothing changes.</strong> That is
/// the shipped state and the state of every environment and test host: the reader is not started, the
/// API starts exactly as it did before, and one information line says the reader is off.</para>
/// </summary>
public sealed class NayaxSqsOptions
{
    public const string SectionName = "NayaxSqs";

    /// <summary>
    /// The main queue's URL. Absent means "no queue": the reader does not start at all.
    /// </summary>
    public string? QueueUrl { get; set; }

    /// <summary>
    /// The dead-letter queue's URL, read only for its depth. The reader never receives from or
    /// deletes from it; replaying it is the owner's own AWS console procedure.
    /// </summary>
    public string? DeadLetterQueueUrl { get; set; }

    /// <summary>The AWS region system name the queues live in, for example <c>ap-southeast-2</c>.</summary>
    public string? Region { get; set; }

    /// <summary>The reader IAM user's access key id. An identifier rather than a secret, and still never logged.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// The reader IAM user's secret access key. A secret: never logged, never in a response, never in
    /// a validation message and never in the repository.
    /// </summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// The queue's visibility timeout in seconds, defaulting to
    /// <see cref="NayaxSqsQueuePolicy.DefaultVisibilityTimeoutSeconds"/> - the value parent #1100's
    /// AWS setup specifies for the main queue. It must match the queue's own setting, which is why the
    /// application has a value for it at all rather than relying on the queue's.
    /// </summary>
    public int VisibilityTimeoutSeconds { get; set; } = NayaxSqsQueuePolicy.DefaultVisibilityTimeoutSeconds;
}

/// <summary>
/// The validated form of <see cref="NayaxSqsOptions"/>, and the implementation of
/// <see cref="INayaxSqsReaderSettings"/> the reader sees (issue #1103).
///
/// <para><strong>Half a queue is a refusal, not a default.</strong> A deployment that set a queue URL
/// and no region or no credentials has asked for the reader and would get an API that silently
/// imports no sales, so registration fails at startup naming the missing setting - the same
/// fail-closed shape as <c>NayaxLynxConfiguration</c>'s base-URL check. A deployment that set
/// <em>nothing</em> is not a misconfiguration at all and starts normally.</para>
///
/// <para><strong>No message carries a value.</strong> A refusal names the setting that is missing and
/// never prints a URL, a key id or a secret.</para>
/// </summary>
public sealed class NayaxSqsConfiguration : INayaxSqsReaderSettings
{
    private NayaxSqsConfiguration(
        string? queueUrl,
        string? deadLetterQueueUrl,
        string? region,
        string? accessKeyId,
        string? secretAccessKey,
        TimeSpan visibilityTimeout)
    {
        QueueUrl = queueUrl;
        DeadLetterQueueUrl = deadLetterQueueUrl;
        Region = region;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        VisibilityTimeout = visibilityTimeout;
    }

    /// <summary>The main queue URL, or <see langword="null"/> when no queue is configured.</summary>
    public string? QueueUrl { get; }

    /// <summary>The dead-letter queue URL, or <see langword="null"/> when none is configured.</summary>
    public string? DeadLetterQueueUrl { get; }

    /// <summary>The AWS region system name, or <see langword="null"/> when no queue is configured.</summary>
    public string? Region { get; }

    /// <summary>The reader IAM user's access key id, or <see langword="null"/> when no queue is configured.</summary>
    public string? AccessKeyId { get; }

    /// <summary>The reader IAM user's secret access key, or <see langword="null"/> when no queue is configured.</summary>
    public string? SecretAccessKey { get; }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(QueueUrl);

    /// <inheritdoc />
    public bool HasDeadLetterQueue => !string.IsNullOrWhiteSpace(DeadLetterQueueUrl);

    /// <inheritdoc />
    public TimeSpan VisibilityTimeout { get; }

    /// <summary>
    /// Validates <paramref name="options"/> and answers the configuration the reader runs on.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A queue URL is configured without the region and the credential pair the reader needs to
    /// reach it.
    /// </exception>
    public static NayaxSqsConfiguration From(NayaxSqsOptions? options)
    {
        var visibilityTimeout = NayaxSqsQueuePolicy.VisibilityTimeout(
            options?.VisibilityTimeoutSeconds ?? NayaxSqsQueuePolicy.DefaultVisibilityTimeoutSeconds);

        var queueUrl = Trimmed(options?.QueueUrl);
        if (queueUrl is null)
        {
            return new NayaxSqsConfiguration(null, null, null, null, null, visibilityTimeout);
        }

        var region = Require(options?.Region, nameof(NayaxSqsOptions.Region));
        var accessKeyId = Require(options?.AccessKeyId, nameof(NayaxSqsOptions.AccessKeyId));
        var secretAccessKey = Require(options?.SecretAccessKey, nameof(NayaxSqsOptions.SecretAccessKey));

        return new NayaxSqsConfiguration(
            queueUrl,
            Trimmed(options?.DeadLetterQueueUrl),
            region,
            accessKeyId,
            secretAccessKey,
            visibilityTimeout);
    }

    private static string Require(string? value, string setting) =>
        Trimmed(value)
        ?? throw new InvalidOperationException(
            $"{SettingName(setting)} must be configured because {SettingName(nameof(NayaxSqsOptions.QueueUrl))} "
                + "is. Provision it as an App Service application setting or a Key Vault reference, or "
                + $"remove {SettingName(nameof(NayaxSqsOptions.QueueUrl))} to leave the Nayax SQS sales "
                + "reader switched off.");

    private static string SettingName(string setting) =>
        $"{NayaxSqsOptions.SectionName}:{setting}";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
