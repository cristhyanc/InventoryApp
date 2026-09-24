using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>
/// One durable record of what the business bootstrap actually did to one table (issue #64,
/// checkpoint 3).
///
/// The backfill is a high-risk, one-way data operation on real financial and inventory history,
/// so it does not just log and exit: it writes its own evidence, inside the same transaction as
/// the change. That makes the operation reviewable after the fact ("which rows moved, when, to
/// which business, and did the totals match?") rather than only at the moment an operator
/// watched the console.
///
/// It is deliberately NOT <see cref="IBusinessOwned"/>. It is an operational audit of the
/// tenancy rollout itself, not business data, and it must stay readable while diagnosing a
/// bootstrap that assigned rows to the wrong business - precisely the case where a tenant filter
/// would hide the evidence.
/// </summary>
public class BusinessBackfillAudit
{
    public int Id { get; set; }

    /// <summary>Groups every row written by a single bootstrap invocation.</summary>
    public Guid RunId { get; set; }

    /// <summary>The business the rows were assigned to.</summary>
    public int BusinessId { get; set; }

    /// <summary>The physical table the rows were assigned in.</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>Rows that were unassigned before this run touched the table.</summary>
    public long UnassignedRowsBefore { get; set; }

    /// <summary>Rows this run assigned to the business.</summary>
    public long RowsAssigned { get; set; }

    /// <summary>
    /// Rows still unassigned after the run. A completed bootstrap leaves this at zero; anything
    /// else means the run was partial and must be investigated before the next step.
    /// </summary>
    public long UnassignedRowsAfter { get; set; }

    /// <summary>Total rows in the table, which the backfill must never change.</summary>
    public long TotalRowsBefore { get; set; }

    public long TotalRowsAfter { get; set; }

    [JsonIgnore]
    public DateTime RecordedAtUtc { get; set; }
}
