namespace InventoryApi.DTOs;

/// <summary>
/// What <c>POST api/imports/nayax-sales-history</c> answers with (issue #1304): the id of the job the
/// accepted upload became, and nothing else at all.
///
/// <para>The reply is deliberately this small. The upload has imported nothing yet, so there is no
/// count to report and nothing to claim about the file's contents beyond its having been accepted;
/// the Imports page follows the job with <c>GET api/imports/nayax-sales-history/jobs</c>, where the
/// progress the background job records is reported.</para>
/// </summary>
/// <param name="JobId">The stored job's own key.</param>
public sealed record NayaxSalesHistoryUploadAccepted(int JobId);
