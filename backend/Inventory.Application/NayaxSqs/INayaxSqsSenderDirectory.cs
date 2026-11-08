using Inventory.Domain.Tenancy;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What the one cross-business sender lookup answers: which business an AWS IAM user sends for,
/// whether that mapping is switched on, and where that business is in its lifecycle (issue #1103).
///
/// <para><strong>Three values, and deliberately nothing else.</strong> The projection is the whole of
/// what makes the lookup safe. Two of them come from the tenant-owned sender row and are the whole of
/// what the owner's approval of 5 November 2026 permits: the ownership key - which is not business
/// data - and the enabled flag. The third is that business's own lifecycle status, read from
/// <c>Businesses</c>, which carries no tenant filter at all. No sender id is echoed back, no
/// last-message instant, no revision, no row instant, no name, and nothing about what the business
/// trades.</para>
/// </summary>
/// <param name="BusinessId">The business the sender id is recorded against.</param>
/// <param name="IsEnabled">Whether that mapping is in force.</param>
/// <param name="BusinessStatus">
/// That business's lifecycle status, so "is this business still trading?" is answered by the same
/// statement rather than by a second read the first one could disagree with.
/// </param>
public sealed record NayaxSqsSenderAttribution(
    int BusinessId,
    bool IsEnabled,
    BusinessStatus BusinessStatus);

/// <summary>
/// "Whose sale is this?" - the one question the shared Nayax SQS queue has to answer before any
/// business scope exists (issue #1103).
///
/// <para><strong>It is one method and it is read-only.</strong> A received message carries the SQS
/// <c>SenderId</c> system attribute and no business, so the lookup has nothing to narrow itself by:
/// that is exactly why the sender id is unique <em>globally</em> rather than per business (issue
/// #1101, AGENTS.md § Tenant ownership and data isolation - Uniqueness is per business, with exactly
/// one declared exception). The adapter behind this port makes one <c>IgnoreQueryFilters</c>
/// statement over the sender table, projecting only that row's business key and enabled flag, and it
/// is the <strong>eighth</strong> reviewed filter-ignoring path and the first one that is not in a
/// request at all - reviewed on this issue, under the owner's approval of 5 November 2026 (AGENTS.md
/// § Tenant ownership and data isolation).</para>
///
/// <para><strong>No request path may reach it.</strong> The only caller is the in-process queue
/// reader's own loop, which is not a request and resolves no actor; there is no controller, no
/// endpoint and no use case on a request path that depends on this port. It takes no predicate, no
/// column list, no page and no sort, and it answers about at most one business, because at most one
/// business can hold a sender id.</para>
///
/// <para>Everything else a message needs - the connection, the plan, the machines, the sale and the
/// rebuild - happens inside a service scope resolved to the one business this answered with, where
/// the ordinary tenant query filters and <c>BusinessOwnershipEnforcer</c> are fully in force.</para>
///
/// <para><strong>Issue #1104 adds a second question to the same port, and it is the same question
/// turned around:</strong> "which businesses are on the queue at all?", which the hourly
/// reconciliation needs so it knows whose <c>lastSales</c> to compare. It is a second statement in
/// this one reviewed adapter under the repository owner's approval of 5 November 2026, and it
/// projects <strong>business ids only</strong> - see
/// <see cref="ListEligibleBusinessIdsAsync"/>.</para>
/// </summary>
public interface INayaxSqsSenderDirectory
{
    /// <summary>
    /// The business <paramref name="senderId"/> is recorded against, or <see langword="null"/> when
    /// no business has recorded it - an unknown sender, which is a message this application must not
    /// attribute to anybody.
    /// </summary>
    Task<NayaxSqsSenderAttribution?> FindBusinessBySenderAsync(
        string? senderId, CancellationToken cancellationToken);

    /// <summary>
    /// The businesses whose Nayax SQS sender is switched on and whose own lifecycle is approved and
    /// active - the businesses the hourly reconciliation of issue #1104 runs for.
    ///
    /// <para><strong>It projects business ids and nothing else at all.</strong> No sender id, no
    /// enabled flag, no instant, no revision, no name and nothing a business trades. An ownership key
    /// is not business data, and a list of keys is the whole of what a scheduler needs to open one
    /// scope per business - which is the only thing the answer is used for.</para>
    ///
    /// <para><strong>It takes nothing.</strong> No predicate, no page, no sort, no business id and no
    /// sender id: the answer is decided entirely by stored state, so there is no input a caller could
    /// widen it with. The eligibility rule itself is the same one the queue reader already applies
    /// per message - enabled mapping, active business - so "whose sales do we accept?" and "whose
    /// sales do we verify?" cannot drift apart.</para>
    ///
    /// <para>Like <see cref="FindBusinessBySenderAsync"/> it runs in a scope that has resolved no
    /// business, because the businesses are the answer rather than the input, and it is read-only.</para>
    /// </summary>
    Task<IReadOnlyList<int>> ListEligibleBusinessIdsAsync(CancellationToken cancellationToken);
}
