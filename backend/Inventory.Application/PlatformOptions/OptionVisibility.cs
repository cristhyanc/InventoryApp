using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformOptions;

/// <summary>
/// What the application currently offers (issue #873), for the caller asking - which is every
/// signed-in member through <c>GET /api/me/access</c>, and the Super Admin surface that writes it.
///
/// <para><strong>It takes no identifier and no business.</strong> The setting is one shared
/// application-wide answer, so there is nothing to scope it by: the same answer is reported to
/// everybody, and the request carries nothing that could choose a different one.</para>
///
/// <para><strong>"Nothing stored" becomes the default here, in one place.</strong> The store reports
/// the absence of the row as <see langword="null"/>; this use case is the only thing that turns that
/// into <see cref="OptionVisibilitySettings.Default"/>, so an installation that has never switched
/// anything off and one that switched everything back on are answered identically and no reader has
/// to know which it is.</para>
///
/// <para>This answers what is <em>offered</em>, never what is permitted. The classification tool's
/// own endpoints authorize every request by the <c>Data.Repair</c> capability exactly as issue #433
/// left them, whatever this answers.</para>
/// </summary>
public sealed class GetOptionVisibility
{
    private readonly IOptionVisibilityStore _store;

    public GetOptionVisibility(IOptionVisibilityStore store)
    {
        _store = store;
    }

    public async Task<OptionVisibilitySettings> Handle(CancellationToken cancellationToken) =>
        await _store.FindAsync(cancellationToken).ConfigureAwait(false)
            ?? OptionVisibilitySettings.Default;
}

/// <summary>
/// Switches Historical GST Classification on or off for everybody but a Super Admin (issue #873).
///
/// <para><strong>Authority is the boundary's, and it is the platform administrator's alone.</strong>
/// The endpoint that reaches this use case is behind <c>PlatformAdminPolicy</c> and the
/// <c>PlatformDiagnosticsEndpointAttribute</c> marker, so the configured Entra <c>(tid, oid)</c>
/// pair is what has already been established by the time this runs; no business role, membership row
/// or request value can satisfy it, and a refused request never reaches here at all. What this use
/// case adds is the actor's identity for the audit trail - read <em>before</em> the write, so a
/// change is never stored for a request whose actor the audit could not name.</para>
///
/// <para><strong>Visibility only.</strong> It writes one boolean about what a menu offers. It grants
/// nothing, revokes nothing, and touches no purchase, classification, provenance, cost or
/// quantity - the maintenance tool it is about keeps its own <c>Data.Repair</c> authorization and
/// its Preview-then-Apply contract unchanged.</para>
/// </summary>
public sealed class SetHistoricalGstClassificationVisibility
{
    private readonly IOptionVisibilityStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IOptionVisibilityAudit _audit;
    private readonly IClock _clock;

    public SetHistoricalGstClassificationVisibility(
        IOptionVisibilityStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IOptionVisibilityAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>The setting as it was stored.</returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the change for.
    /// </exception>
    public async Task<OptionVisibilitySettings> Handle(bool visible, CancellationToken cancellationToken)
    {
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var updatedAtUtc = _clock.UtcNow;
        var stored = await _store
            .SetHistoricalGstClassificationVisibleAsync(visible, updatedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _audit.Record(new OptionVisibilityAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            stored.HistoricalGstClassificationVisible,
            updatedAtUtc));

        return stored;
    }
}
