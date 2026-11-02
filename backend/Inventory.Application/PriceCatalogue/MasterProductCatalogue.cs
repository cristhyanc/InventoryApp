using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One bounded, searchable page of the shared master-product catalogue (issue #1023, parent #817):
/// what the Super Admin Master products page is built from.
///
/// <para>It owns exactly one decision, and it is deliberately not the client's: how big a page is and
/// which one this is, through <see cref="MasterProductCataloguePaging"/>. Everything else - the order,
/// the search - is fixed by the port behind it, and the answer reports the page that was actually
/// served rather than the one that was asked for.</para>
///
/// <para>This is a read. Nothing here creates, edits or deletes a master product: adding one and
/// correcting one are <see cref="CreateMasterProduct"/>'s and <see cref="UpdateMasterProduct"/>'s
/// (issue #1024), the Super Admin review's own create is issue #928's, and deleting one is a separate
/// issue that follows.</para>
/// </summary>
public sealed class ListMasterProducts
{
    private readonly IMasterProductCatalogueStore _store;

    public ListMasterProducts(IMasterProductCatalogueStore store)
    {
        _store = store;
    }

    public async Task<MasterProductCataloguePage> Handle(
        MasterProductCatalogueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = MasterProductCataloguePaging.Resolve(query.Page, query.PageSize);

        // Blank is the same as absent: a search box a person cleared must not narrow the catalogue to
        // the products whose brand contains a space.
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        var result = await _store
            .ListAsync(
                new MasterProductCatalogueFilter(search, (page - 1) * pageSize, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return new MasterProductCataloguePage(result.Products, result.TotalCount, page, pageSize);
    }
}

/// <summary>
/// One master product of the shared catalogue with its supplier listings (issue #1023): the detail
/// behind a row of the Master products page.
///
/// <para>It is a read of the catalogue and of nothing else: no mapping, no business and no business's
/// product is read to build it, and <see langword="null"/> - an id no master product has - is the
/// endpoint's <c>404</c> rather than an empty product.</para>
/// </summary>
public sealed class GetMasterProduct
{
    private readonly IMasterProductCatalogueStore _store;

    public GetMasterProduct(IMasterProductCatalogueStore store)
    {
        _store = store;
    }

    public Task<MasterProductDetail?> Handle(long masterProductId, CancellationToken cancellationToken) =>
        _store.FindAsync(masterProductId, cancellationToken);
}

/// <summary>
/// Adds one master product to the shared catalogue (issue #1024, parent #817): the Super Admin
/// recording a product before any business has asked for its price.
///
/// <para><strong>It is held to exactly the rules the review's own create is.</strong>
/// <see cref="MasterProductPolicy"/> decides what a brand, a name and a size may be and computes the
/// key two descriptions are compared by, so a product added here and the same product added while
/// deciding a match are one row and not two.</para>
///
/// <para><strong>Nothing but the catalogue changes.</strong> No supplier listing is created,
/// confirmed or refused, no business's tracked product is matched or unmatched, and no business's own
/// product, stock, cost or purchase is read or written - which is the issue's last criterion and the
/// owner's clarification of 31 October 2026.</para>
/// </summary>
public sealed class CreateMasterProduct
{
    private readonly IMasterProductCatalogueWriteStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPriceCatalogueReviewAudit _audit;
    private readonly IClock _clock;

    public CreateMasterProduct(
        IMasterProductCatalogueWriteStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IPriceCatalogueReviewAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>The master product that was created, as the catalogue reports it.</returns>
    /// <exception cref="DomainValidationException">
    /// The brand, name, size or unit is not one this catalogue stores. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// Another master product already describes this brand, name and size. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the change for.
    /// </exception>
    public async Task<CataloguedMasterProduct> Handle(
        MasterProductSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // Read before anything else, so nothing is written for a request whose actor the audit trail
        // could not name. The policy at the boundary has already established that this is the
        // configured platform administrator; this is who they are.
        var actor = MasterProductActor.Current(_actorAccessor);

        // Validated here, before a transaction is opened: a description this catalogue cannot store
        // is refused without taking SQLite's write lock. The key is computed with the four values at
        // once, so a caller cannot validate the description and key something else.
        var description = MasterProductPolicy.Normalise(
            submission.Brand,
            submission.Name,
            submission.SizeValue,
            submission.SizeUnit);

        var changedAtUtc = _clock.UtcNow;

        var created = await _store
            .CreateAsync(description, changedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _audit.Record(MasterProductActor.Entry(
            actor,
            PriceCatalogueReviewAction.MasterProductCreated,
            created.Id,
            changedAtUtc));

        return created;
    }
}

/// <summary>
/// Corrects one master product of the shared catalogue (issue #1024): a wrong brand, a wrong name or
/// a wrong size, on the row every business tracking that product already points at.
///
/// <para><strong>The row keeps its key</strong>, so every tracking mapping, supplier listing, match
/// suggestion and collector work item that names it still names it afterwards. Nothing else is
/// rewritten, no listing is confirmed or refused, and no business's tracked product is matched or
/// unmatched.</para>
///
/// <para><strong>Two refusals are the store's, inside the write transaction</strong>, because both
/// are comparisons against state a page read: a <em>size</em> change while a supplier listing is
/// confirmed (<see cref="MasterProductEditPolicy.SizeChangeRefusedMessage"/> - those pack sizes were
/// confirmed against the current size, and every tracking business's per-unit price would change),
/// and an edit presenting a <c>lastChangedAtUtc</c> the row no longer carries
/// (<see cref="MasterProductEditPolicy.StaleEditMessage"/>). What this use case refuses itself is an
/// edit that carries no expected instant at all, which is an incomplete request rather than a stale
/// one.</para>
/// </summary>
public sealed class UpdateMasterProduct
{
    private readonly IMasterProductCatalogueWriteStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPriceCatalogueReviewAudit _audit;
    private readonly IClock _clock;

    public UpdateMasterProduct(
        IMasterProductCatalogueWriteStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IPriceCatalogueReviewAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// The master product as it now stands, or <see langword="null"/> when no master product has that
    /// id - which the endpoint reports as <c>404</c>.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The submitted description is not one this catalogue stores, or the edit carries no expected
    /// instant. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// Another master product already carries the resulting key, the row has been changed since, or
    /// the size would change while a supplier listing of it is confirmed. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">The request carries no identifiable actor.</exception>
    public async Task<CataloguedMasterProduct?> Handle(
        long masterProductId,
        MasterProductEditSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var actor = MasterProductActor.Current(_actorAccessor);

        var expectedUpdatedAtUtc = MasterProductEditPolicy.RequireExpectedUpdatedAt(
            submission.ExpectedUpdatedAtUtc);

        var description = MasterProductPolicy.Normalise(
            submission.Brand,
            submission.Name,
            submission.SizeValue,
            submission.SizeUnit);

        var changedAtUtc = _clock.UtcNow;

        var updated = await _store
            .UpdateAsync(
                masterProductId,
                new MasterProductEdit(description, expectedUpdatedAtUtc),
                changedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        if (updated is null)
        {
            return null;
        }

        _audit.Record(MasterProductActor.Entry(
            actor,
            PriceCatalogueReviewAction.MasterProductEdited,
            updated.Id,
            changedAtUtc));

        return updated;
    }
}

/// <summary>
/// The two things the catalogue's create and edit do identically (issue #1024): naming the acting
/// administrator, and recording one audit event about the master product that changed.
///
/// <para>Shared so that "who changed a master product, and what did they change" has one shape on
/// both paths. The entry is the review's own (<see cref="PriceCatalogueReviewAuditEntry"/>), because
/// this is the same shared catalogue and an investigation should not have two trails to follow; the
/// fields a review decision fills and a catalogue change has no answer for - the listing, the
/// suggestion, the waiting key, the accepted price and the confirmed pack quantity - are left empty
/// rather than invented.</para>
/// </summary>
internal static class MasterProductActor
{
    internal static ActorIdentity Current(IAuthenticatedActorAccessor accessor) =>
        accessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

    internal static PriceCatalogueReviewAuditEntry Entry(
        ActorIdentity actor,
        PriceCatalogueReviewAction action,
        long masterProductId,
        DateTime occurredAtUtc) =>
        new(
            actor.DirectoryTenantId,
            actor.ObjectId,
            action,
            SupplierListingId: null,
            masterProductId,
            SuggestionId: null,
            WaitingKey: null,
            PriceAccepted: false,
            ConfirmedUnitsPerPack: null,
            occurredAtUtc);
}
