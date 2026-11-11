using Inventory.Application.Imports;

namespace InventoryApi.Adapters.ReimbursementImport;

/// <summary>
/// The host-side half of the uploaded reimbursement report import (issue #1149): the fourth hosted
/// background service this application runs, beside <c>Auth/MemberProfileRefreshWorker</c> (issue
/// #524), <c>Adapters/NayaxSqs/NayaxSqsSalesReaderWorker</c> (issue #1103) and
/// <c>Adapters/NayaxReconciliation/NayaxSalesReconciliationWorker</c> (issue #1104).
///
/// <para><strong>It is hosting glue and holds no rule.</strong> Everything it does is call
/// <see cref="ReimbursementImportJobRunner.RunAsync"/> with the host's stopping token. Which job is
/// next, what an archive may contain, what one file's import means and when a job gives up are all
/// decided in <c>Inventory.Application</c> and <c>Inventory.Domain</c>; the reason this class exists
/// at all is that <c>BackgroundService</c> is a hosting type and a loop started by the host is a
/// hosting concern.</para>
///
/// <para><strong>It is in-process, and that is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads is explicit: put the work in
/// <c>Inventory.Application</c> as a use case behind narrow ports and invoke it from the thinnest
/// possible adapter. The production store is one SQLite file with one writer on the App Service's own
/// mount, so a second host writing it is outside the supported envelope; running here keeps one
/// writer and needs no second identity, configuration surface or telemetry source. App Service
/// <strong>Always On</strong> is what keeps the process alive to pick jobs up, and is the same human
/// prerequisite the queue reader has (README.md § The Reimbursement Report import).</para>
///
/// <para><strong>Nothing it does can stop the host.</strong> The runner turns every per-job failure
/// into a recorded outcome or a logged line and returns on cancellation; this class adds no
/// <c>catch</c> of its own because there is nothing left for one to do.</para>
/// </summary>
public sealed class ReimbursementImportJobWorker : BackgroundService
{
    private readonly ReimbursementImportJobRunner _runner;

    public ReimbursementImportJobWorker(ReimbursementImportJobRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _runner.RunAsync(stoppingToken);
}
