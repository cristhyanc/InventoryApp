using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Suppliers;

/// <summary>
/// The platform supplier records this business may add, for the Suppliers page's "Available to add"
/// section (issue #935, third task of #817).
///
/// <para>Which business is never asked: it comes from the authenticated actor's membership through
/// <see cref="ICurrentBusinessProvider"/>, and the use case takes no parameter at all - no filter,
/// no search, no page. What it publishes is the enabled records that serve this business's recorded
/// state and that it holds no copy of; a business with no recorded state sees the nationwide records
/// alone.</para>
/// </summary>
public sealed class ListAvailableSupplierPresets
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAvailableSupplierPresetStore _store;

    public ListAvailableSupplierPresets(
        ICurrentBusinessProvider currentBusiness,
        IAvailableSupplierPresetStore store)
    {
        _currentBusiness = currentBusiness;
        _store = store;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no business to answer for.
    /// </exception>
    public async Task<IReadOnlyList<AvailableSupplierPreset>> Handle(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _store.ListAvailableAsync(businessId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Adds one of those records to the caller's own business as a linked copy (issue #935).
///
/// <para><strong>The request carries one platform record id and nothing else.</strong> No name, no
/// website, no business: the name and the website are read from the record, the business is the
/// caller's own, and the link is what makes both of them the master's from the moment the row exists
/// (issue #934). There is therefore nothing a caller can submit that decides what is stored.</para>
///
/// <para><strong>It is an ordinary supplier write.</strong> It runs in the caller's resolved
/// business scope, under the Suppliers controller's existing supplier-write capability, through the
/// central ownership stamp - not through issue #849's provisioning exception, whose limits this
/// issue leaves exactly as they are. A read-only support session and a role preview refuse it by
/// their own method rules, like every other non-<c>GET</c> request.</para>
/// </summary>
public sealed class AddSupplierFromPreset
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAvailableSupplierPresetStore _store;

    public AddSupplierFromPreset(
        ICurrentBusinessProvider currentBusiness,
        IAvailableSupplierPresetStore store)
    {
        _currentBusiness = currentBusiness;
        _store = store;
    }

    /// <summary>
    /// Adds the record with <paramref name="presetId"/> to the caller's business.
    ///
    /// <para><strong>Eligibility is not checked here, and that is the point.</strong> Whether the
    /// record is still suggested, whether it still serves this business's recorded state, and
    /// whether this business already holds a copy are decided by <c>CopyAsync</c> itself, with the
    /// write lock held, in the same operation as the insert. An answer this use case read first
    /// would be a stale read by the time the row was written - a Super Admin disabling the record, a
    /// change of its served states, an Owner changing the business's state, or another tab's add all
    /// commit on their own schedule - and acting on it is how a business acquires a supplier nothing
    /// entitles it to. So the three outcomes come back as outcomes, and this use case's only job is
    /// to name the business they apply to.</para>
    ///
    /// <para><strong>The unique index is still the backstop.</strong> The filtered index of issue
    /// #934 on <c>(BusinessId, SupplierPresetId)</c> is what makes "one copy per record" true
    /// against a writer this process did not serialise with - a second process, or a row written by
    /// hand - and when it refuses, the violation is translated into the <em>same</em> "already
    /// added" answer rather than surfacing as a server error. One copy either way.</para>
    /// </summary>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership. Nothing is written.
    /// </exception>
    public async Task<SupplierPresetAddition> Handle(int presetId, CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await _store.CopyAsync(businessId, presetId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_store.IsDuplicateCopy(exception))
        {
            // The index refused a copy the in-lock read did not see. The caller gets the same answer
            // either way, which is what makes it invisible to them rather than a server error - and
            // there is still exactly one copy.
            return SupplierPresetAddition.AlreadyAdded();
        }
    }
}

/// <summary>
/// The top-up a change of the business's state makes (issue #935): a linked copy of every enabled
/// platform supplier record that now serves this business and that it holds no copy of.
///
/// <para><strong>It only ever adds.</strong> Nothing is removed, hidden, relinked or edited, because
/// a copy may already be named on a purchase and a business that moves has still bought what it
/// bought (the owner's decision of 27 October 2026 on #817). A record that no longer matches the
/// new state is simply left where it is.</para>
///
/// <para><strong>It is a use case rather than a method on the store</strong> because the loop and
/// what to do when one copy is not made are decisions, not statements: a record another request
/// copied between the list and the insert is skipped, because the outcome this top-up wanted - one
/// copy of it, which now exists - has already happened, and a record that stopped being offered in
/// between is skipped because it is no longer one this business is entitled to. Reporting either as
/// a failure would make an Owner's state change fail over a supplier they have, or over a platform
/// record they never asked for.</para>
///
/// <para><strong>Each copy decides its own eligibility</strong> (<c>CopyAsync</c>, inside the write
/// lock), so the list this loop walks is a candidate list rather than a decision. The list and the
/// copies normally run inside the settings save's own write transaction, where nothing can commit
/// between them at all; the per-copy check is what keeps the rule true when they do not - this same
/// use case called outside that transaction, or a database another process is writing.</para>
///
/// <para><strong>Which business is passed in, not resolved here.</strong> Its one caller is
/// <c>UpdateBusinessSettings</c>, which has already resolved the caller's own business from their
/// membership and is holding the write transaction this must run inside - so the top-up and the
/// state it belongs to commit together or not at all.</para>
/// </summary>
public sealed class AddMatchingSupplierPresets
{
    private readonly IAvailableSupplierPresetStore _store;

    public AddMatchingSupplierPresets(IAvailableSupplierPresetStore store)
    {
        _store = store;
    }

    /// <returns>How many copies were created; zero is the normal answer and the common one.</returns>
    public async Task<int> Handle(BusinessId businessId, CancellationToken cancellationToken)
    {
        var offered = await _store.ListAvailableAsync(businessId, cancellationToken).ConfigureAwait(false);

        var added = 0;
        foreach (var preset in offered)
        {
            try
            {
                var copy = await _store
                    .CopyAsync(businessId, preset.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (copy.Outcome == SupplierPresetAdditionOutcome.Added)
                {
                    added++;
                }

                // Anything else is skipped rather than counted or reported: "already added" means
                // the one copy this top-up wanted exists, and "not available" means the record
                // stopped being one this business may hold between the list and the write. Neither
                // is a reason to fail an Owner's state change.
            }
            catch (Exception exception) when (_store.IsDuplicateCopy(exception))
            {
                // The unique index refusing a copy this business acquired in between, from a writer
                // the in-lock read could not see. Skipped for the same reason: one copy exists,
                // which is the rule, and the state change carries on.
            }
        }

        return added;
    }
}
