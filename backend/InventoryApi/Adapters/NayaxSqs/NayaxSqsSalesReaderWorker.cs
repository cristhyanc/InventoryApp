using Inventory.Application.NayaxSqs;

namespace InventoryApi.Adapters.NayaxSqs;

/// <summary>
/// The host-side half of the Nayax SQS sales reader (issue #1103): the second hosted background
/// service this application runs, beside <c>Auth/MemberProfileRefreshWorker</c> (issue #524).
///
/// <para><strong>It is hosting glue and holds no rule.</strong> Everything it does is call
/// <see cref="NayaxSqsSalesReader.RunAsync"/> with the host's stopping token. When to receive, when
/// to extend a message's visibility, when to delete, when to back off and what any of it means are
/// all decided in <c>Inventory.Application</c> and <c>Inventory.Domain</c>; the reason this class
/// exists at all is that <c>BackgroundService</c> is a hosting type and a loop started by the host is
/// a hosting concern.</para>
///
/// <para><strong>It is in-process, and that is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads lists "a genuinely event-driven
/// trigger the API cannot receive" as one of the things that would reopen the no-Functions decision -
/// and an SQS queue the API <em>can</em> receive from is the other side of that test. The production
/// store is one SQLite file with one writer on the App Service's own mount, so a second host writing
/// it is outside the supported envelope; receiving in the API keeps one writer, needs no second
/// identity, configuration surface or telemetry source, and reuses the business's own credentials
/// exactly as a request does. App Service <strong>Always On</strong> is what keeps the process alive
/// to poll, and is a human prerequisite.</para>
///
/// <para><strong>Nothing it does can stop the host.</strong> The reader turns every failure into a
/// disposition or a bounded, jittered backoff, and returns on cancellation; this class adds no
/// <c>catch</c> of its own because there is nothing left for one to do.</para>
/// </summary>
public sealed class NayaxSqsSalesReaderWorker : BackgroundService
{
    private readonly NayaxSqsSalesReader _reader;

    public NayaxSqsSalesReaderWorker(NayaxSqsSalesReader reader)
    {
        _reader = reader;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _reader.RunAsync(stoppingToken);
}
