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

/// <summary>
/// Whether one platform supplier record is one this business may add (issue #935), as the answer a
/// copy is decided from.
/// </summary>
public enum SupplierPresetAvailability
{
    /// <summary>
    /// There is no such record to add: no record with that id, one that is no longer suggested, or
    /// one that does not serve this business's state. All three are one answer on purpose - a member
    /// of one business learns nothing about the platform's list beyond what the "Available to add"
    /// list already told them.
    /// </summary>
    NotOffered,

    /// <summary>
    /// The record serves this business and it holds no copy of it: a copy may be created.
    /// </summary>
    Available,

    /// <summary>
    /// The record serves this business and it already holds a copy. It is the answer to a
    /// double-click, a second tab, and a state change that committed between the list and the add.
    /// </summary>
    AlreadyCopied,
}

/// <summary>How a request to add one platform supplier record ended (issue #935).</summary>
public enum SupplierPresetAdditionOutcome
{
    /// <summary>The copy was created, linked to the record it came from.</summary>
    Added,

    /// <summary>
    /// There is no such record to add; see <see cref="SupplierPresetAvailability.NotOffered"/>.
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
/// platform supplier records that serve a business's state and that it holds no copy of, and the
/// two ways a copy comes into existence afterwards.
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
/// <para><strong>At most one copy per business per record</strong>, which is the filtered unique
/// index issue #934 added on <c>(BusinessId, SupplierPresetId)</c>. The availability answer is the
/// pre-check that turns the ordinary case into a clear refusal, and the index is what makes the rule
/// true when two requests race - which is why <see cref="IsDuplicateCopy"/> exists, exactly as
/// <c>ISupplierPresetStore.IsDuplicateName</c> does for the platform list's own name rule.</para>
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
    /// Whether the record with <paramref name="presetId"/> is one
    /// <paramref name="businessId"/> may add, judged by the same state rule
    /// <see cref="ListAvailableAsync"/> applies and re-read now: the list a caller is looking at may
    /// be stale, and a record disabled, re-pointed at other states or already copied in between is
    /// not one to add.
    /// </summary>
    Task<SupplierPresetAvailability> FindAvailableAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="businessId"/>'s own copy of the record with
    /// <paramref name="presetId"/>: a <c>Supplier</c> with that record's name, its website and its
    /// id, exactly as the approval's copy writes one - no contact detail, no GST default, not
    /// hidden, and nothing written back to the platform table.
    /// </summary>
    /// <returns>The supplier as it was stored.</returns>
    /// <exception cref="Exception">
    /// The unique index refused a second copy of that record, which <see cref="IsDuplicateCopy"/>
    /// recognises. Nothing is written, and the caller is answered
    /// <see cref="SupplierPresetAdditionOutcome.AlreadyAdded"/> rather than a server error.
    /// </exception>
    Task<SupplierRecord> CopyAsync(
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
