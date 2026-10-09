using System.Text.Json;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Application.SaleTimestampRepair;

/// <summary>
/// Previews repairing stored Nayax sale instants from authoritative source evidence (issue #472).
///
/// It changes no sale, no amount, no status, no product mapping, no costing value and no stock
/// movement. The one row it writes is the plan itself: a draft the operator's later Apply names, so
/// the apply works from the server's own decisions and provenance instead of anything a caller
/// submits. The draft is tenant-owned, single-use and expiring, and the apply additionally re-reads
/// the sales it names and refuses the plan if any of them changed (AGENTS.md § Database and
/// migrations: a repair is explicit, previewable, idempotent and observable).
///
/// What it is for is deciding, before anything is written, whether a change to historical financial
/// records is justified: every examined transaction with its old and new UTC instant and Sydney
/// business date and the source that decided it, the revenue that moves between Sydney days, the
/// products whose costing would be replayed and from when, the transactions no source covered, the
/// export transactions this business holds no sale for, and - against a fixed cutoff - how the
/// repaired state reconciles with the export it is being compared to.
/// </summary>
public sealed class PreviewNayaxSaleTimestampRepair
{
    private readonly INayaxSaleTimestampRepairStore _store;
    private readonly NayaxSaleTimestampRepairProjection _projection;

    public PreviewNayaxSaleTimestampRepair(
        INayaxSaleTimestampRepairStore store,
        INayaxLynxClient nayax,
        INayaxSalesWorkbookReader workbook,
        IBusinessCalendar calendar,
        IClock clock)
    {
        _store = store;
        _projection = new NayaxSaleTimestampRepairProjection(store, nayax, workbook, calendar, clock);
    }

    /// <summary>
    /// Builds the plan and stores it as a draft.
    /// </summary>
    /// <param name="request">Which sources to read and the reconciliation window to report.</param>
    /// <param name="evidenceExport">
    /// An optional operator-supplied Nayax transaction export, read for its
    /// <c>AuthorizationDateTimeGMT</c> column only. Nothing in it is imported: it creates no sale and
    /// updates no amount, status or product mapping. It is the only supported source for a
    /// transaction older than the live rolling window, including one before the start of the
    /// reconciliation week, because the Lynx API publishes no date-ranged sales endpoint carrying the
    /// authorization instant.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public async Task<NayaxSaleTimestampRepairPreview> Handle(
        NayaxSaleTimestampRepairPreviewRequest request,
        NayaxSalesFileInput? evidenceExport = null,
        CancellationToken cancellationToken = default)
    {
        var (preview, plan) = await _projection.BuildAsync(
            request, evidenceExport, Guid.NewGuid(), cancellationToken);

        _store.AddDraft(new(preview.PreviewId, JsonSerializer.Serialize(plan), preview.CreatedAt, preview.ExpiresAt));
        await _store.SaveChangesAsync(cancellationToken);
        return preview;
    }
}
