using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

// The "Set GST rule in bulk" Preview/Apply contracts (issue #1076), owned by the Application layer
// the way the historical GST classification's are. They are deliberately tiny on the way in: the
// server decides which products are eligible from its own read, so a caller has nothing to supply
// but the rule it wants and the count it confirmed.

/// <summary>
/// How many of this business's products currently carry no GST rule, computed without persisting
/// anything.
///
/// <para><see cref="EligibleCount"/> is the value <see cref="SetBulkProductGstRule"/> requires back.
/// It is the whole concurrency token: the apply re-counts inside its own write transaction and
/// refuses a count that has moved, so a change is only ever applied to the set the operator was
/// shown. Zero is an answer - every product already has a rule - and never an absence.</para>
///
/// <para>It is the business's whole catalogue, active and inactive products alike, and no search,
/// category or status filter narrows it: the scope the operator confirms is "every product of this
/// business with no rule", which is what the dialog says in so many words.</para>
/// </summary>
public sealed record BulkProductGstRulePreview(int EligibleCount);

/// <summary>
/// The confirmation of a bulk GST rule preview: the rule to set, and the eligible count it was
/// confirmed against.
///
/// <para>There is deliberately nothing else here - no business, no product id, no product list, no
/// filter and no changed count. The business is resolved from the authenticated actor's membership
/// (AGENTS.md § Tenant ownership and data isolation), the eligible products are the server's own
/// read, and the changed count is what the write reports rather than what a caller asks for.</para>
///
/// <para><see cref="ExpectedEligibleCount"/> is nullable so that "the caller did not send this" is
/// distinguishable from "the caller sent zero". A missing count is refused rather than treated as a
/// confirmation of nothing (<see cref="BulkProductGstRulePolicy.RequireSubmittedEligibleCount"/>).
/// </para>
/// </summary>
public sealed record SetBulkProductGstRuleRequest(
    GstClassification GstRule,
    int? ExpectedEligibleCount);

/// <summary>
/// What the apply actually wrote: the rule it configured, and how many products it configured.
///
/// <para><see cref="ChangedCount"/> is the database's own answer - the rows the write touched -
/// rather than the confirmed count echoed back, so the summary an operator reads is what happened
/// and not what was asked for.</para>
/// </summary>
public sealed record BulkProductGstRuleApplied(GstClassification GstRule, int ChangedCount);
