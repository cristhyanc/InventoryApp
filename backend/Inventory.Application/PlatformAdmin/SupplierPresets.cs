using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Every platform supplier preset, enabled and disabled alike, ordered by name (issue #848).
///
/// <para>It takes no filter, page, sort or search parameter, for the reason
/// <see cref="ListPlatformBusinesses"/> takes none: a caller-supplied predicate is how a fixed
/// platform list becomes a query engine. The disabled ones are included deliberately - a Super Admin
/// cannot re-enable a preset they cannot see.</para>
/// </summary>
public sealed class ListSupplierPresets
{
    private readonly ISupplierPresetStore _store;

    public ListSupplierPresets(ISupplierPresetStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<SupplierPresetRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListOrderedByNameAsync(cancellationToken);
}

/// <summary>
/// What a Super Admin submits about one preset, before any rule has been applied to it (issues #848
/// and #931): everything a create carries, and everything an edit carries except whether the preset
/// stays suggested.
///
/// <para>It is one parameter object rather than seven positional arguments because the supplier-master
/// fields of #931 are not independent of each other: served states are checked against each other,
/// and the connector key is checked against the connector. Handing them over together is what lets
/// one use case validate one coherent submission, and it keeps the two use cases' signatures the same
/// shape as the resource they write.</para>
///
/// <para>Every value here is <em>submitted</em> and nothing here is trusted: the names mirror the
/// request body, the types are as permissive as the wire is, and the policies in
/// <c>Inventory.Domain.Suppliers</c> decide what is storable. <c>IsEnabled</c> is deliberately not
/// part of it - a create never carries one, because a new preset is created suggested.</para>
/// </summary>
/// <param name="Name">The submitted name.</param>
/// <param name="Url">The submitted website, or <see langword="null"/>/blank for none.</param>
/// <param name="ServesNationwide">Whether the caller claims the whole country.</param>
/// <param name="ServedStates">The submitted state codes, in any order, case or padding.</param>
/// <param name="PricesIncludeGst">Whether the caller says the published prices include GST.</param>
/// <param name="PriceConnector">The submitted collection route, which may be an undeclared number.</param>
/// <param name="PriceConnectorKey">The submitted connector key.</param>
public sealed record SupplierPresetSubmission(
    string? Name,
    string? Url,
    bool ServesNationwide,
    IReadOnlyList<string?>? ServedStates,
    bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey);

/// <summary>
/// Adds one platform supplier preset (issue #848, with the supplier-master fields of #931).
///
/// <para><strong>The name rule is enforced twice, and the two are not redundant.</strong> The
/// pre-check turns the ordinary case - a Super Admin typing a name that is already in the list -
/// into a <c>409</c> with a sentence they can act on. The unique index on the stored normalised name
/// is what makes the rule true: between the pre-check and the insert another request can store the
/// same name, and a <c>WHERE</c> clause cannot see a row that does not exist yet. So the insert's
/// own constraint violation is caught and translated into the <em>same</em> <c>409</c>, which is why
/// two concurrent creates of one name leave one row and one conflict rather than one row and one
/// <c>500</c>.</para>
///
/// <para>The audit event is emitted after the write, never before, and carries no name or URL text;
/// see <see cref="SupplierPresetAuditEntry"/>.</para>
/// </summary>
public sealed class CreateSupplierPreset
{
    private readonly ISupplierPresetStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly ISupplierPresetAudit _audit;
    private readonly IClock _clock;

    public CreateSupplierPreset(
        ISupplierPresetStore store,
        IAuthenticatedActorAccessor actorAccessor,
        ISupplierPresetAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <exception cref="DomainValidationException">
    /// The name is blank or too long, the URL is not an absolute http(s) address or is too long, or
    /// the served states or the connector and its key are not a combination
    /// <see cref="SupplierPresetMasterPolicy"/> accepts. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// Another preset already holds this name, ignoring case. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the change for.
    /// </exception>
    public async Task<SupplierPresetRecord> Handle(
        SupplierPresetSubmission submitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);

        // Read before anything else, so a preset is never written for a request whose actor the
        // audit trail could not name. The policy at the boundary has already established that this
        // is the configured platform administrator; this is who they are.
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var validatedName = SupplierPresetPolicy.NormaliseName(submitted.Name);
        var normalisedName = SupplierPresetPolicy.NormalisedName(validatedName);
        var validatedUrl = SupplierPresetWebsite.Normalise(submitted.Url);

        // Every supplier-master rule runs here, before the duplicate pre-check and before anything is
        // written: a refused combination leaves the list exactly as it was.
        var servedStates = SupplierPresetMasterPolicy.NormaliseServedStates(
            submitted.ServesNationwide,
            submitted.ServedStates);
        var connectorKey = SupplierPresetMasterPolicy.NormaliseConnectorKey(
            submitted.PriceConnector,
            submitted.PriceConnectorKey);

        if (await _store
            .ExistsWithNormalisedNameAsync(normalisedName, excludingId: null, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new DomainConflictException(SupplierPresetPolicy.DuplicateNameMessage);
        }

        var createdAtUtc = _clock.UtcNow;
        SupplierPresetRecord stored;
        try
        {
            stored = await _store
                .AddAsync(
                    new NewSupplierPreset(
                        validatedName,
                        normalisedName,
                        validatedUrl,
                        submitted.ServesNationwide,
                        servedStates,
                        submitted.PricesIncludeGst,
                        submitted.PriceConnector,
                        connectorKey,
                        createdAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (_store.IsDuplicateName(exception))
        {
            // The pre-check passed and the index refused: another request stored this name in
            // between. The caller gets the same answer either way, which is what makes the race
            // invisible to them rather than a server error.
            throw new DomainConflictException(SupplierPresetPolicy.DuplicateNameMessage);
        }

        _audit.Record(SupplierPresetAuditEntry.For(
            actor.DirectoryTenantId,
            actor.ObjectId,
            SupplierPresetChange.Created,
            stored,
            createdAtUtc));

        return stored;
    }
}

/// <summary>
/// Edits one platform supplier preset (issue #848, with the supplier-master fields of #931): its
/// name, its website, what it serves, how its prices are collected and whether it is still
/// suggested, in one <c>PUT</c> of the whole resource.
///
/// <para>Enabling and disabling are this operation, deliberately rather than two extra endpoints:
/// these fields are what a preset is, they are all editable by the same authority, and a
/// separate toggle would be a second write path over one row with its own race to reason about.</para>
///
/// <para><strong>Every field is submitted together, which is what makes the supplier-master rules
/// decidable.</strong> Whether nationwide and a state list conflict, and whether the connector key
/// belongs to the connector, are questions about a whole submission; a partial edit would have to
/// answer them against values it had not been given.</para>
///
/// <para>The name rule is the one <see cref="CreateSupplierPreset"/> applies, with this preset's own
/// row excluded - without the exclusion no preset could ever be saved without renaming it. Changing
/// only the URL or only the enabled flag therefore passes, because the name it still holds is its
/// own.</para>
///
/// <para>Nothing here deletes a preset, and nothing here reaches a business: a preset a business
/// already copied is that business's own supplier row, and it is not re-synchronised, renamed or
/// disabled by an edit here (issue #848 excludes that explicitly).</para>
/// </summary>
public sealed class UpdateSupplierPreset
{
    private readonly ISupplierPresetStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly ISupplierPresetAudit _audit;
    private readonly IClock _clock;

    public UpdateSupplierPreset(
        ISupplierPresetStore store,
        IAuthenticatedActorAccessor actorAccessor,
        ISupplierPresetAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// The preset as the edit left it, or <see langword="null"/> when no preset with
    /// <paramref name="id"/> exists.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The name is blank or too long, the URL is not an absolute http(s) address or is too long, or
    /// the served states or the connector and its key are not a combination
    /// <see cref="SupplierPresetMasterPolicy"/> accepts. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// Another preset already holds this name, ignoring case. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor.
    /// </exception>
    public async Task<SupplierPresetRecord?> Handle(
        int id,
        SupplierPresetSubmission submitted,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);

        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var validatedName = SupplierPresetPolicy.NormaliseName(submitted.Name);
        var normalisedName = SupplierPresetPolicy.NormalisedName(validatedName);
        var validatedUrl = SupplierPresetWebsite.Normalise(submitted.Url);

        var servedStates = SupplierPresetMasterPolicy.NormaliseServedStates(
            submitted.ServesNationwide,
            submitted.ServedStates);
        var connectorKey = SupplierPresetMasterPolicy.NormaliseConnectorKey(
            submitted.PriceConnector,
            submitted.PriceConnectorKey);

        if (await _store
            .ExistsWithNormalisedNameAsync(normalisedName, excludingId: id, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new DomainConflictException(SupplierPresetPolicy.DuplicateNameMessage);
        }

        var updatedAtUtc = _clock.UtcNow;
        SupplierPresetRecord? stored;
        try
        {
            stored = await _store
                .UpdateAsync(
                    id,
                    new SupplierPresetEdit(
                        validatedName,
                        normalisedName,
                        validatedUrl,
                        isEnabled,
                        submitted.ServesNationwide,
                        servedStates,
                        submitted.PricesIncludeGst,
                        submitted.PriceConnector,
                        connectorKey,
                        updatedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (_store.IsDuplicateName(exception))
        {
            throw new DomainConflictException(SupplierPresetPolicy.DuplicateNameMessage);
        }

        if (stored is null)
        {
            // No such preset. Nothing was written, so there is nothing to audit.
            return null;
        }

        _audit.Record(SupplierPresetAuditEntry.For(
            actor.DirectoryTenantId,
            actor.ObjectId,
            SupplierPresetChange.Updated,
            stored,
            updatedAtUtc));

        return stored;
    }
}

/// <summary>
/// A preset's optional website, normalised by the <em>one</em> supplier-website rule there is:
/// issue #847's <see cref="SupplierUrlPolicy"/>, which a business's own suppliers use through
/// <see cref="Inventory.Application.Suppliers.CreateSupplier"/> and
/// <see cref="Inventory.Application.Suppliers.UpdateSupplier"/>. A preset and a supplier therefore
/// cannot disagree about what a valid address is, which matters because issue #835 child C is to
/// copy the one into the other.
///
/// <para>The rule reports a field-level message rather than throwing, because the suppliers surface
/// answers it as a <c>ValidationProblemDetails</c> against <c>url</c>. This surface answers a
/// refusal with the <c>400</c> and the sentence <c>DomainExceptionHandler</c> already gives every
/// other preset refusal, so the message is raised as the exception that carries it - the same
/// shape, and the same wording, as the name rules beside it.</para>
/// </summary>
internal static class SupplierPresetWebsite
{
    /// <exception cref="DomainValidationException">
    /// The address is not an absolute http(s) one, or is longer than
    /// <see cref="SupplierUrlPolicy.MaxLength"/> characters. Nothing is written.
    /// </exception>
    internal static string? Normalise(string? url) =>
        SupplierUrlPolicy.TryNormalize(url, out var normalised, out var error)
            ? normalised
            : throw new DomainValidationException(error!);
}
