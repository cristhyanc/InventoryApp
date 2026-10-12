using Inventory.Application.Tenancy;
using Inventory.Application.Time;

namespace Inventory.Application.Members;

/// <summary>
/// The current business's invites, for the Owner who manages them (issue #531).
///
/// It takes no identifier at all: the invites are read through the <c>AppDbContext</c> tenant query
/// filters, and the business those filters apply comes from the authenticated actor's membership, so
/// no route, query, body or header value can choose whose invites are listed. It still resolves the
/// business explicitly first, so a caller whose membership does not resolve is <em>refused</em>
/// rather than told their business has no invites - "nothing to show" and "you may not look" must
/// not be the same answer.
///
/// <para>Every state is listed, including used, cancelled and expired invites: the list is the
/// record of who was invited and what became of it, and hiding the closed ones would leave an Owner
/// unable to see that a link they sent was used.</para>
/// </summary>
public sealed class ListMemberInvites
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IMemberInviteStore _store;
    private readonly IClock _clock;

    public ListMemberInvites(
        ICurrentBusinessProvider currentBusiness,
        IMemberInviteStore store,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _store = store;
        _clock = clock;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no business whose invites could be listed.
    /// </exception>
    public async Task<IReadOnlyList<MemberInvite>> Handle(CancellationToken cancellationToken)
    {
        _ = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);

        var rows = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        // One instant for the whole list, so two invites that expire a tick apart are not judged
        // against two different "nows" within one response (MemberInvitePolicy owns the rule).
        var nowUtc = _clock.UtcNow;

        return [.. rows.Select(row => MemberInvite.From(row, nowUtc))];
    }
}
