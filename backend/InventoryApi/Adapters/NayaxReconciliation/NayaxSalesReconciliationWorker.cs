using Inventory.Application.NayaxReconciliation;

namespace InventoryApi.Adapters.NayaxReconciliation;

/// <summary>
/// The host-side half of the hourly Nayax sales reconciliation (issue #1104): the third hosted
/// background service this application runs, beside <c>Auth/MemberProfileRefreshWorker</c> (issue
/// #524) and <c>Adapters/NayaxSqs/NayaxSqsSalesReaderWorker</c> (issue #1103).
///
/// <para><strong>It is hosting glue and holds no rule.</strong> Everything it does is call
/// <see cref="NayaxSalesReconciliationRunner.RunAsync"/> with the host's stopping token. When to
/// sweep, which businesses are eligible, what is compared and what any of it means are all decided in
/// <c>Inventory.Application</c> and <c>Inventory.Domain</c>; the reason this class exists at all is
/// that <c>BackgroundService</c> is a hosting type and a loop started by the host is a hosting
/// concern.</para>
///
/// <para><strong>It is in-process, and that is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads is explicit about scheduled work
/// under the current decision: put the work in <c>Inventory.Application</c> as a use case behind
/// narrow ports and invoke it from the thinnest possible adapter. The production store is one SQLite
/// file with one writer on the App Service's own mount, so a second host writing it is outside the
/// supported envelope; running here keeps one writer, needs no second identity, configuration surface
/// or telemetry source, and reads each business's own Nayax credentials exactly as a request does.
/// App Service <strong>Always On</strong> is what keeps the process alive to sweep, and is a human
/// prerequisite (README.md § Nayax SQS sales reader).</para>
///
/// <para><strong>Nothing it does can stop the host.</strong> The runner turns every per-business
/// failure into an outcome or a logged line and returns on cancellation; this class adds no
/// <c>catch</c> of its own because there is nothing left for one to do.</para>
/// </summary>
public sealed class NayaxSalesReconciliationWorker : BackgroundService
{
    private readonly NayaxSalesReconciliationRunner _runner;

    public NayaxSalesReconciliationWorker(NayaxSalesReconciliationRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _runner.RunAsync(stoppingToken);
}
