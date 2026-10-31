using Inventory.Domain.Tenancy;

namespace Inventory.Application.Suppliers;

/// <summary>
/// One platform supplier record a business may add, as the Application layer sees it (issue #935):
/// its id, its name and its website, and deliberately nothing else.
///
/// <para>This is the whole of what a member-facing endpoint publishes about a platform table. The
/// id is what an add names, the name is what the person reads, and the website is what makes two
/// similarly named suppliers distinguishable; what the record claims about its market, its price
/// basis and its collection route is platform configuration a business has no use for and is not
/// carried here (issue #931).</para>
/// </summary>
/// <param name="Id">The platform record's own key, which an add request names.</param>
/// <param name="Name">The record's name, as the Super Admin typed it.</param>
/// <param name="Url">Its website, or <see langword="null"/> when none is recorded.</param>
public sealed record AvailableSupplierPreset(int Id, string Name, string? Url);

/// <summary>How a request to add one platform supplier record ended (issue #935).</summary>
public enum SupplierPresetAdditionOutcome
{
    /// <summary>The copy was created, linked to the record it came from.</summary>
    Added,

    /// <summary>
    /// There is no such record to add: no record with that id, one that is no longer suggested, or
    /// one that does not serve this business's state. All three are one answer on purpose - a member
    /// of one business learns nothing about the platform's list beyond what the "Available to add"
    /// list already told them. It is also the answer when one of the three became true between the
    /// list the caller was looking at and the copy, because the copy decides it for itself.
    /// </summary>
    NotAvailable,

    /// <summary>
    /// This business already holds a copy of that record, so there is nothing to add. It is reported
    /// as a conflict rather than silently succeeding, because "one copy per record" is a rule the
    /// caller is entitled to be told about - and never as a server error.
    /// </summary>
    AlreadyAdded,
}

/// <summary>
/// The answer an add gives the HTTP boundary, in the shape <see cref="SupplierWriteResult"/>
/// already uses: an outcome the controller maps to a status code, and the created supplier when
/// there is one.
/// </summary>
public sealed record SupplierPresetAddition(
    SupplierPresetAdditionOutcome Outcome,
    SupplierRecord? Supplier)
{
    public static SupplierPresetAddition Added(SupplierRecord supplier) =>
        new(SupplierPresetAdditionOutcome.Added, supplier);

    public static SupplierPresetAddition NotAvailable() =>
        new(SupplierPresetAdditionOutcome.NotAvailable, null);

    public static SupplierPresetAddition AlreadyAdded() =>
        new(SupplierPresetAdditionOutcome.AlreadyAdded, null);
}

/// <summary>
/// The narrow port behind the "Available to add" suppliers of issue #935 (third task of #817): the
/// platform supplier records that serve a business's state and that it holds no copy of, and the one
/// write both ways a copy comes into existence - the one-click add and the state change's top-up -
/// go through.
///
/// <para><strong>It is a member-facing read of a platform table, and that is a reviewed tenancy
/// decision.</strong> Until this issue no business endpoint knew the supplier records existed, and
/// the only exception was issue #873's single boolean on <c>GET /api/me/access</c>. This is the
/// second, reviewed on #935 and bounded by this port: enabled records only, three columns, no
/// caller-supplied filter, predicate, page or sort, and nothing about any other business - a
/// platform record is about no business at all, so there is none to leak. See AGENTS.md § Tenant
/// ownership and data isolation and docs/architecture.md § Tenant ownership.</para>
///
/// <para><strong>Every write here is an ordinary write in the caller's own business scope.</strong>
/// It is deliberately <em>not</em> the provisioning exception of issue #849: that one exists because
/// an approval runs with a denied scope and has no other way to equip the business it is approving,
/// while both writes here happen inside a request that resolved its own business from the caller's
/// membership. So the central query filters and <c>BusinessOwnershipEnforcer</c> apply unchanged,
/// the new row is stamped by the enforcer rather than by this port, and no business id a request
/// supplied reaches anything: the one every method here takes is always the one tenancy resolution
/// produced.</para>
///
/// <para><strong>Nothing a caller submits is written.</strong> A request carries one platform record
/// id and nothing else; the name, the website and the link are read from that record by the adapter
/// itself, which is why <see cref="CopyAsync"/> takes an id rather than values a caller could have
/// chosen.</para>
///
/// <para><strong>Eligibility is decided by the copy itself, not before it.</strong> There is
/// deliberately no "is this record available?" method beside <see cref="CopyAsync"/>: a list a
/// caller is looking at, and any answer given before the write, are stale reads by definition, and a
/// record the Super Admin disables or re-points - or a state the Owner changes - between such an
/// answer and the insert would otherwise add a supplier nothing entitles the business to. So the
/// enabled flag, the served states, the business's own state and the existing copy are all read
/// inside the write lock the insert holds, and a changed answer comes back as
/// <see cref="SupplierPresetAdditionOutcome.NotAvailable"/> or
/// <see cref="SupplierPresetAdditionOutcome.AlreadyAdded"/> rather than as a failure. The only read
/// that stands on its own is <see cref="ListAvailableAsync"/>, which is a page's contents and
/// decides nothing.</para>
///
/// <para><strong>At most one copy per business per record</strong>, which is the filtered unique
/// index issue #934 added on <c>(BusinessId, SupplierPresetId)</c>. The in-lock answer is what turns
/// the ordinary case into a clear response, and the index is the backstop under it for a writer this
/// process did not serialise with - which is why <see cref="IsDuplicateCopy"/> exists, exactly as
/// <c>IBusinessMembershipWriteStore.IsDuplicateActiveMembership</c> does under that path's own
/// in-lock check and <c>ISupplierPresetStore.IsDuplicateName</c> does for the platform list's own
/// name rule.</para>
/// </summary>
public interface IAvailableSupplierPresetStore
{
    /// <summary>
    /// The enabled platform records that serve <paramref name="businessId"/>'s recorded state - or
    /// the nationwide ones alone, when it has none - and that this business holds no copy of,
    /// ordered by name.
    ///
    /// <para>It takes no filter, search, page or sort: a caller-supplied predicate over a fixed
    /// platform list is how a list becomes a query engine, which is the reason
    /// <c>ISupplierPresetStore.ListOrderedByNameAsync</c> takes none either. The business's state is
    /// read from its own row, never from the request.</para>
    /// </summary>
    Task<IReadOnlyList<AvailableSupplierPreset>> ListAvailableAsync(
        BusinessId businessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Decides whether <paramref name="businessId"/> may hold a copy of the record with
    /// <paramref name="presetId"/>, and writes it, as <strong>one atomic operation</strong>.
    ///
    /// <para><strong>The authoritative eligibility read and the insert are one step.</strong>
    /// Whether the record is still suggested, whether it still serves this business, what state the
    /// business now records, and whether a copy already exists are all read with the write lock
    /// already held, so nothing can commit in between: a disable, a change of served states, a
    /// change of the business's state and another copy either land before the lock and are seen, or
    /// wait behind it. That is the whole reason this method takes an id rather than values and
    /// answers rather than assuming - a caller cannot hold an eligibility answer that is still true.
    /// </para>
    ///
    /// <para>What is stored is that record's name, its website and its id, exactly as the approval's
    /// copy stores one - no contact detail, no GST default, not hidden, and nothing written back to
    /// the platform table.</para>
    /// </summary>
    /// <returns>
    /// <see cref="SupplierPresetAdditionOutcome.Added"/> with the stored supplier;
    /// <see cref="SupplierPresetAdditionOutcome.NotAvailable"/> when the record is not one this
    /// business may add, by the time the lock was held; or
    /// <see cref="SupplierPresetAdditionOutcome.AlreadyAdded"/> when it already holds a copy.
    /// Eligibility that changed is one of those answers and never an exception.
    /// </returns>
    /// <exception cref="Exception">
    /// The unique index refused the copy regardless - the backstop for a writer this process did not
    /// serialise with - which <see cref="IsDuplicateCopy"/> recognises. Nothing is written, and the
    /// caller is answered <see cref="SupplierPresetAdditionOutcome.AlreadyAdded"/> rather than a
    /// server error.
    /// </exception>
    Task<SupplierPresetAddition> CopyAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the filtered unique index on
    /// <c>(BusinessId, SupplierPresetId)</c> refusing a second copy, and nothing else.
    ///
    /// <para>It is asked of the adapter rather than decided in a use case because recognising a
    /// provider's constraint violation is provider knowledge: an EF or SQLite type must not reach
    /// <c>Inventory.Application</c>. Any other database failure is left untranslated rather than
    /// reported to a caller as "already added".</para>
    /// </summary>
    bool IsDuplicateCopy(Exception exception);
}
