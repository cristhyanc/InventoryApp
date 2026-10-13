using Inventory.Application.Businesses;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth;

/// <summary>
/// The boundary implementation of <see cref="ISupportSessionAccessor"/> (issue #551): the one place
/// that reads <see cref="SupportSessionHeader"/>'s value and turns it into the business a request
/// may read.
///
/// <para><strong>Both halves are required, and both are this request's own.</strong> A request joins
/// a session only when <see cref="PlatformAdminPolicy"/> succeeds for it - re-evaluated here, on
/// this request, never inferred from the fact that the authorization middleware ran - and the
/// presented id matches a session that is <em>that same actor's</em>, still active, and for a
/// business that is still <see cref="BusinessStatus.Active"/>. Four separate things can make it
/// unusable (it was never theirs, it was ended, it expired, the business was switched off) and all
/// four answer the same way: <see cref="SupportSessionAccessState.Ended"/>, which the pipeline turns
/// into <c>403</c> with <see cref="SupportSessionErrorCodes.SessionEnded"/>. There is deliberately
/// no fall back to the administrator's own membership - they have none, and a caller who presented a
/// dead session must be told so rather than quietly served as themselves.</para>
///
/// <para><strong>A caller the platform-admin policy refuses never has this header read at all.</strong>
/// That keeps an ordinary member exactly as they were: setting the header gains them nothing, and
/// loses them nothing but the cosmetic profile refresh it has suppressed since issue #524. Reading
/// the value for them would mean a member's request could be refused by a session rule that has
/// nothing to do with them.</para>
///
/// <para><strong>Memoised, so the question is asked once.</strong> The capability authorization
/// handler asks before the endpoint is chosen, <see cref="BusinessScopeMiddleware"/> asks to publish
/// the scope, and the use cases ask again through <c>CurrentCapabilities</c> and
/// <c>GET /api/me/access</c>. One lookup answers all of them, so they cannot disagree and the
/// presented id is hashed once.</para>
///
/// <para><strong>Why the authorization service is resolved from the request rather than
/// injected.</strong> One of this type's consumers is
/// <see cref="BusinessCapabilityAuthorizationHandler"/>, which the container builds as part of
/// <c>IAuthorizationService</c>'s own dependency graph. Taking that service in the constructor would
/// make the graph circular and fail at resolution; taking it from the request's own service provider
/// is the same instance, obtained at the moment it is used.</para>
/// </summary>
public sealed class RequestSupportSessionAccessor : ISupportSessionAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly ISupportSessionStore _sessions;
    private readonly IBusinessProfileStore _businesses;
    private readonly IClock _clock;
    private readonly ILogger<RequestSupportSessionAccessor> _logger;
    private SupportSessionAccess? _access;

    public RequestSupportSessionAccessor(
        IHttpContextAccessor httpContextAccessor,
        IAuthenticatedActorAccessor actorAccessor,
        ISupportSessionStore sessions,
        IBusinessProfileStore businesses,
        IClock clock,
        ILogger<RequestSupportSessionAccessor> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _actorAccessor = actorAccessor;
        _sessions = sessions;
        _businesses = businesses;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SupportSessionAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_access is not null)
        {
            return _access;
        }

        return _access = await ResolveOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SupportSessionAccess> ResolveOnceAsync(CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext;

        if (context is null)
        {
            // No request at all - the background profile-refresh scope, for instance. Nothing is
            // joining a session there.
            return SupportSessionAccess.NotRequested;
        }

        if (!context.Request.Headers.TryGetValue(SupportSessionHeader.Name, out var presented))
        {
            return SupportSessionAccess.NotRequested;
        }

        var sessionId = presented.ToString();

        if (context.User.Identity is not { IsAuthenticated: true })
        {
            return SupportSessionAccess.NotRequested;
        }

        var platformAdmin = await context.RequestServices
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(context.User, resource: null, PlatformAdminPolicy.Name)
            .ConfigureAwait(false);

        if (!platformAdmin.Succeeded)
        {
            // Not a platform administrator, so this header means nothing on this request and its
            // value is not read. An ordinary member stays an ordinary member.
            return SupportSessionAccess.NotRequested;
        }

        if (_actorAccessor.GetCurrentActor().Actor is not { } actor)
        {
            // The policy succeeded, so this is practically unreachable - it is the same parse. It
            // fails closed anyway rather than looking up a session for an actor nobody identified.
            return Ended("the caller's own identity could not be read");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Ended("the presented session id was blank");
        }

        var session = await _sessions
            .FindActiveForActorByIdHashAsync(
                actor,
                SupportSessionId.Hash(sessionId),
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return Ended("the presented session id matches no active session of this caller's");
        }

        if (!BusinessId.TryFrom(session.BusinessId, out var businessId))
        {
            return Ended("the session's stored business key is not a usable one");
        }

        // The business's current state, read now rather than trusted from when the session started:
        // a deactivation between two requests has to close the view on the next one, which is what
        // makes the deactivation hook of issue #550 a belt rather than the only strap.
        var business = await _businesses.FindAsync(businessId, cancellationToken).ConfigureAwait(false);

        if (business is null)
        {
            return Ended("the session's business row could not be read");
        }

        // The same Domain rule a start is judged by, on purpose: a business a session could not have
        // been started for is not one it may go on reading.
        if (!SupportSessionPolicy.CanStartFor(business.Status))
        {
            return Ended("the session's business is no longer active");
        }

        return SupportSessionAccess.Active(
            session.BusinessId,
            business.Name,
            session.ExpiresAtUtc,
            session.Key);
    }

    /// <summary>
    /// Records why a presented session was refused, for an operator, and answers with the one state
    /// the caller is told about.
    ///
    /// The reason is logged and never returned: the four ways a session can be unusable are told
    /// apart here and deliberately not on the wire, because three of them are facts about somebody
    /// else's session or about a business the caller is no longer entitled to anything about. The
    /// log carries no session id in either form, no business name and no actor identifier - the
    /// request's own correlation id is what ties this to the caller.
    /// </summary>
    private SupportSessionAccess Ended(string reason)
    {
        _logger.LogWarning(
            "A support session was refused for this request: {SupportSessionDenialReason}.",
            reason);

        return SupportSessionAccess.Ended;
    }
}
