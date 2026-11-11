using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformOptions;

/// <summary>
/// One catalogue destination and the state it is in now (issue #1148), whether that state was
/// stored or defaulted - a caller cannot tell the two apart, and nothing it does should depend on
/// which it was.
/// </summary>
/// <param name="Key">The destination's catalogue key.</param>
/// <param name="Group">Its menu group, from the catalogue (issue #1153).</param>
/// <param name="Label">Its display label, from the catalogue.</param>
/// <param name="Visible">
/// Whether it is offered to members who are not the platform administrator. A Super Admin keeps
/// every destination in both states.
/// </param>
public sealed record MenuVisibilityOptionState(string Key, string Group, string Label, bool Visible);

/// <summary>
/// What the application currently offers, destination by destination (issue #1148).
///
/// <para>It is the whole catalogue every time, in catalogue order, so a reader never has to know
/// whether a key had a stored row. <see cref="SwitchedOffKeys"/> is the narrower answer every
/// signed-in caller is given through <c>GET /api/me/access</c>: naming only what is switched off
/// keeps that response the same size it is today in the state every installation is in, where
/// nothing is off at all.</para>
/// </summary>
/// <param name="Options">Every catalogue destination with its resolved state, in catalogue order.</param>
public sealed record MenuVisibility(IReadOnlyList<MenuVisibilityOptionState> Options)
{
    /// <summary>
    /// The keys that are switched off, in catalogue order, and empty when nothing is - which is what
    /// every existing installation reports.
    /// </summary>
    public IReadOnlyList<string> SwitchedOffKeys { get; } =
        [.. Options.Where(option => !option.Visible).Select(option => option.Key)];

    /// <summary>
    /// Whether the destination <paramref name="key"/> names is offered. A key the catalogue does not
    /// declare reads as offered, for the reason an absent row does: nothing has switched it off.
    /// </summary>
    public bool IsVisible(string key) => !SwitchedOffKeys.Contains(key, StringComparer.Ordinal);
}

/// <summary>
/// What the application currently offers (issue #1148, replacing issue #873's
/// <c>GetOptionVisibility</c>), for the caller asking - which is every signed-in member through
/// <c>GET /api/me/access</c>, and the Super Admin surface that lists and writes it.
///
/// <para><strong>It takes no identifier and no business.</strong> The setting is one shared
/// application-wide answer, so there is nothing to scope it by: the same answer is reported to
/// everybody, and the request carries nothing that could choose a different one.</para>
///
/// <para><strong>"Nothing stored" becomes On here, in one place.</strong> The store reports only the
/// rows that exist; this use case is the only thing that turns a missing row into
/// <see langword="true"/>, so an installation that has never switched anything and one that switched
/// everything back on are answered identically and no reader has to know which it is. A stored key
/// the catalogue no longer declares is ignored for the same reason the catalogue is closed: what
/// exists is the catalogue's decision, never a row's.</para>
///
/// <para>This answers what is <em>offered</em>, never what is permitted. Every destination's own
/// endpoints keep authorizing each request by their own capability, plan and provider rules,
/// whatever this answers.</para>
/// </summary>
public sealed class GetMenuVisibility
{
    private readonly IMenuVisibilityStore _store;

    public GetMenuVisibility(IMenuVisibilityStore store)
    {
        _store = store;
    }

    public async Task<MenuVisibility> Handle(CancellationToken cancellationToken)
    {
        var stored = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var switchedOff = stored
            .Where(row => !row.Visible)
            .Select(row => row.Key)
            .ToHashSet(StringComparer.Ordinal);

        return new MenuVisibility(
            [.. MenuVisibilityCatalogue.Options.Select(option => new MenuVisibilityOptionState(
                option.Key,
                option.Group,
                option.Label,
                !switchedOff.Contains(option.Key)))]);
    }
}

/// <summary>
/// Switches one menu destination on or off for everybody but a Super Admin (issue #1148, replacing
/// issue #873's <c>SetHistoricalGstClassificationVisibility</c>).
///
/// <para><strong>Authority is the boundary's, and it is the platform administrator's alone.</strong>
/// The endpoints that reach this use case are behind <c>PlatformAdminPolicy</c> and the
/// <c>PlatformDiagnosticsEndpointAttribute</c> marker, so the configured Entra <c>(tid, oid)</c>
/// pair is what has already been established by the time this runs; no business role, membership row
/// or request value can satisfy it, and a refused request never reaches here at all. What this use
/// case adds is the key's own validity and the actor's identity for the audit trail - the key first,
/// so an unknown one is refused before anything is read about who asked, and the actor before the
/// write, so a change is never stored for a request whose actor the audit could not name.</para>
///
/// <para><strong>One key per call, named by the caller and checked against the catalogue.</strong>
/// The key is the only thing a caller chooses, and it chooses a <em>declared</em> destination or
/// nothing: an unknown key is answered as not found with nothing stored, which is what keeps this a
/// closed catalogue rather than a feature-flag framework. The write names that one key, so a
/// concurrent write to another destination cannot be overwritten by it.</para>
///
/// <para><strong>Visibility only.</strong> It writes one boolean about what a menu offers. It grants
/// nothing, revokes nothing, and touches no purchase, classification, provenance, cost or
/// quantity - every destination it is about keeps its own authorization unchanged.</para>
/// </summary>
public sealed class SetMenuVisibility
{
    private readonly IMenuVisibilityStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IMenuVisibilityAudit _audit;
    private readonly IClock _clock;

    public SetMenuVisibility(
        IMenuVisibilityStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IMenuVisibilityAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// The destination as it is now stored, or <see langword="null"/> when the catalogue declares no
    /// such key - in which case nothing was stored and nothing was audited.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the change for.
    /// </exception>
    public async Task<MenuVisibilityOptionState?> Handle(
        string key,
        bool visible,
        CancellationToken cancellationToken)
    {
        if (MenuVisibilityCatalogue.Find(key) is not { } option)
        {
            return null;
        }

        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var updatedAtUtc = _clock.UtcNow;
        var stored = await _store
            .SetAsync(option.Key, visible, updatedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _audit.Record(new MenuVisibilityAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            option.Key,
            stored,
            updatedAtUtc));

        return new MenuVisibilityOptionState(option.Key, option.Group, option.Label, stored);
    }
}
