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
    /// <para><strong>The availability check is enforced twice, and the two are not
    /// redundant.</strong> The pre-check turns the ordinary cases - a record this business already
    /// has, a record the platform disabled or re-pointed since the page was loaded - into an answer
    /// the caller can act on. The filtered unique index of issue #934 is what makes "one copy per
    /// record" true: between the check and the insert another request, or a state change's top-up,
    /// can create that copy, and a <c>WHERE</c> clause cannot see a row that does not exist yet. So
    /// the insert's own constraint violation is translated into the <em>same</em> "already added"
    /// answer, which is why two simultaneous adds leave one copy and one conflict rather than one
    /// copy and a server error.</para>
    /// </summary>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership. Nothing is written.
    /// </exception>
    public async Task<SupplierPresetAddition> Handle(int presetId, CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var availability = await _store
            .FindAvailableAsync(businessId, presetId, cancellationToken)
            .ConfigureAwait(false);

        switch (availability)
        {
            case SupplierPresetAvailability.NotOffered:
                return SupplierPresetAddition.NotAvailable();
            case SupplierPresetAvailability.AlreadyCopied:
                return SupplierPresetAddition.AlreadyAdded();
        }

        try
        {
            return SupplierPresetAddition.Added(
                await _store.CopyAsync(businessId, presetId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (_store.IsDuplicateCopy(exception))
        {
            // The pre-check passed and the index refused: this copy was created in between. The
            // caller gets the same answer either way, which is what makes the race invisible to them
            // rather than a server error - and there is still exactly one copy.
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
/// what to do when one copy is refused are decisions, not statements: a record another request
/// copied between the list and the insert is skipped, because the outcome this top-up wanted - one
/// copy of it, which now exists - has already happened. Reporting it as a failure would make an
/// Owner's state change fail over a supplier they have.</para>
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
                await _store.CopyAsync(businessId, preset.Id, cancellationToken).ConfigureAwait(false);
                added++;
            }
            catch (Exception exception) when (_store.IsDuplicateCopy(exception))
            {
                // The unique index refusing a copy this business already acquired in between. It is
                // skipped rather than counted: one copy exists, which is the rule, and the state
                // change carries on.
            }
        }

        return added;
    }
}
