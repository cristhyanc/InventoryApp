namespace InventoryApi.DTOs;

/// <summary>
/// What <c>POST api/nayax-sales-sync</c> answers when this business's sales were synchronized moments
/// ago, so the run was skipped (issue #843).
///
/// It is a <c>200</c> with this body rather than a fourth meaning for the endpoint's existing
/// statuses: <c>204</c> still means "the synchronization ran" and <c>429</c> still means "too many
/// runs too recently" (issue #681), both unchanged for every existing caller. A skip is neither of
/// those - nothing ran, and nothing was refused - and the Dashboard has to be able to tell the three
/// apart to say "Sales synced N min ago" instead of a warning.
///
/// <para><see cref="LastSyncedAtUtc"/> is a true instant, as every other <c>*AtUtc</c> field in this
/// API is; the client renders it in the business's own timezone. It is the instant the last fully
/// successful run <em>finished</em>, which is what the operator is being told about the figures in
/// front of them. The response carries no business id, no operator id and no credential revision:
/// the freshness key is the server's own, and a caller has no use for either half of it.</para>
/// </summary>
/// <param name="WasRecentlySynced">
/// Always <see langword="true"/> in this body, so a client reads the answer from the payload rather
/// than inferring it from the status code alone.
/// </param>
/// <param name="LastSyncedAtUtc">When the last fully successful synchronization finished.</param>
public sealed record NayaxSalesRecentlySyncedResponse(bool WasRecentlySynced, DateTime LastSyncedAtUtc);
