namespace InventoryApi.DTOs;

/// <summary>
/// What <c>POST api/imports/reimbursement-report</c> answers with (issue #1149): the id of the job
/// the accepted upload became, and nothing else at all.
///
/// <para>The reply is deliberately this small. The upload has imported nothing yet, so there is no
/// count to report and nothing to claim about the archive's contents; the Reimbursement Report page
/// (issue #1152) follows the job with the status endpoints, using this id.</para>
/// </summary>
/// <param name="JobId">The stored job's own key.</param>
public sealed record ReimbursementReportUploadAccepted(int JobId);
