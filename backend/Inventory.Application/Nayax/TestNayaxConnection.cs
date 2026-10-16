using System.Text.Json;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Nayax;

/// <summary>
/// What one completed connection test produced (issue #536): the opaque draft id the credentials
/// were held under, when that draft stops existing, and every check line.
///
/// It carries no token and no ciphertext. The whole point of the draft is that the token an Owner
/// pasted stays on the server, so a result that echoed it back - even to the Owner who typed it -
/// would put it into a response body, a browser cache and whatever logs sit in between.
/// </summary>
/// <param name="DraftId">The opaque identifier the tested credentials are held under.</param>
/// <param name="ExpiresAtUtc">When that draft stops existing.</param>
/// <param name="Report">Every check, in order, with its result.</param>
public sealed record NayaxConnectionTestResult(
    Guid DraftId,
    DateTime ExpiresAtUtc,
    NayaxConnectionCheckReport Report);

/// <summary>
/// The outcome of asking for a connection test (issue #536): it ran, or the business has run too
/// many too recently.
///
/// The rate limit is reported as a result rather than thrown as an exception because it is an
/// expected answer that the HTTP boundary turns into a specific status and <c>Retry-After</c>
/// header, not a failure any other caller would want to catch.
/// </summary>
public sealed record NayaxConnectionTestOutcome
{
    private NayaxConnectionTestOutcome(NayaxConnectionTestResult? result, TimeSpan? retryAfter)
    {
        Result = result;
        RetryAfter = retryAfter;
    }

    /// <summary>The completed test, or <see langword="null"/> when it was refused.</summary>
    public NayaxConnectionTestResult? Result { get; }

    /// <summary>
    /// How long until this business may test again, or <see langword="null"/> when the test ran.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public static NayaxConnectionTestOutcome Completed(NayaxConnectionTestResult result) =>
        new(result, null);

    public static NayaxConnectionTestOutcome RateLimited(TimeSpan retryAfter) =>
        new(null, retryAfter);
}

/// <summary>
/// Tests a pasted Nayax operator id and access token and holds them as the current business's
/// short-lived encrypted draft (issue #536, a slice of #506 under #328).
///
/// <para><strong>It is read-only against Nayax and almost read-only locally.</strong> The checks are
/// the <c>GET</c> calls of <c>NayaxConnectionCheckPolicy</c> and nothing else - no
/// <c>POST machines/{id}/machineProducts</c>, no import, no catalogue sync. The one row it writes is
/// the draft. It does not store the connection, does not change
/// <c>NayaxConnectionStatus</c>, does not clear or set a last-tested instant, and does not touch the
/// business's existing credentials in any way: an Owner testing a new token must not be able to
/// break the one that is working. Accepting a draft permanently is issue #537's separate, reviewed
/// step.</para>
///
/// <para><strong>Nothing about the owner comes from the request.</strong> The business is resolved
/// from the authenticated actor's membership through <see cref="ICurrentBusinessProvider"/> and the
/// actor from <see cref="IAuthenticatedActorAccessor"/>; the request body carries exactly the
/// operator id and the token being tested and nothing else. The draft is bound to both, so neither
/// another business nor another member of the same business can pick it up.</para>
///
/// <para><strong>The token is a secret from the first line to the last.</strong> It is validated,
/// handed to the runner for the calls, and handed to the store to be encrypted at rest. It is never
/// logged, never put in an exception message, never serialized into the stored results and never
/// returned. <see cref="NayaxConnectionCredential"/> is used to carry it precisely because its
/// <c>ToString</c> redacts.</para>
/// </summary>
public sealed class TestNayaxConnection
{
    /// <summary>
    /// How long a draft lives. Long enough for an Owner to read eight result lines and decide, short
    /// enough that an abandoned browser tab does not leave a decryptable token sitting in the
    /// database for the rest of the day.
    /// </summary>
    public static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(15);

    private readonly INayaxConnectionCheckRunner _runner;
    private readonly INayaxConnectionTestDraftStore _drafts;
    private readonly NayaxConnectionTestRateLimiter _rateLimiter;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public TestNayaxConnection(
        INayaxConnectionCheckRunner runner,
        INayaxConnectionTestDraftStore drafts,
        NayaxConnectionTestRateLimiter rateLimiter,
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _runner = runner;
        _drafts = drafts;
        _rateLimiter = rateLimiter;
        _currentBusiness = currentBusiness;
        _actors = actors;
        _clock = clock;
    }

    /// <param name="operatorId">The Nayax operator id to test. Not a secret.</param>
    /// <param name="accessToken">The Lynx API access token to test. A secret.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="DomainValidationException">
    /// The operator id or the token is missing or blank. Refused before anything is sent to Nayax
    /// and before a rate-limit allowance is spent, because a blank credential tests nothing.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no resolvable business, or no identity a draft could be bound to.
    /// </exception>
    public async Task<NayaxConnectionTestOutcome> Handle(
        string? operatorId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        var trimmedOperatorId = (operatorId ?? string.Empty).Trim();
        if (trimmedOperatorId.Length == 0)
        {
            throw new DomainValidationException("Enter the Nayax operator id to test.");
        }

        if (!NayaxOperatorId.IsValid(trimmedOperatorId))
        {
            // Refused before any Nayax call and before a rate-limit allowance is spent (issue #681).
            // Nayax's own contract declares the operator path parameter as an int64, and an id that
            // is not digits would be interpolated into a request path: the two operator-scoped
            // checks conclude "that operator is not yours" from a 404, so an id that could address
            // another endpoint could make that conclusion wrong. The message names no part of the
            // submitted value.
            throw new DomainValidationException(NayaxOperatorId.InvalidMessage);
        }

        // Only the outer whitespace of a pasted token is removed. Nothing else about it is
        // normalized, inspected or validated here: its shape is Nayax's contract, and the test is
        // how we find out whether it is a usable one.
        var trimmedToken = (accessToken ?? string.Empty).Trim();
        if (trimmedToken.Length == 0)
        {
            // The message names the field and never any part of the submitted value.
            throw new DomainValidationException("Enter the Nayax access token to test.");
        }

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);
        var actor = RequireActor();

        if (!_rateLimiter.TryClaim(businessId, out var retryAfter))
        {
            // Refused before the runner is reached, so a refused request makes no Nayax call at all.
            return NayaxConnectionTestOutcome.RateLimited(retryAfter);
        }

        // Before the Nayax calls, and outside any transaction (issue #681): a draft this business
        // abandoned, or one a refused save rolled its claim back onto, is unusable the moment it
        // expires and must not keep a decryptable credential ciphertext in the database while this
        // request runs. The replacement below would discard it too, but only once this test has
        // succeeded - a test that Nayax never answers would leave the old one exactly where it was.
        await _drafts.DiscardExpiredAsync(cancellationToken);

        // Revision 0: these credentials have never been stored, so there is no revision to attribute
        // an answer to. Nothing in this use case writes a status result, which is what makes that
        // placeholder safe rather than merely convenient.
        var candidate = new NayaxConnectionCredential(trimmedOperatorId, trimmedToken, credentialRevision: 0);

        var report = await _runner.RunAsync(candidate, cancellationToken);

        var createdAt = _clock.UtcNow;
        var draftId = Guid.NewGuid();
        var expiresAt = createdAt + DraftLifetime;

        await _drafts.ReplaceAsync(
            new NewNayaxConnectionTestDraft(
                draftId,
                candidate,
                JsonSerializer.Serialize(report),
                actor,
                createdAt,
                expiresAt),
            cancellationToken);

        return NayaxConnectionTestOutcome.Completed(new NayaxConnectionTestResult(draftId, expiresAt, report));
    }

    /// <summary>
    /// The draft is bound to the Owner who created it, so a caller this application cannot identify
    /// has nothing to bind it to. Denying is the fail-closed answer; storing a token against an
    /// unidentified actor would make it everybody's.
    /// </summary>
    private ActorIdentity RequireActor() =>
        _actors.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.UnidentifiableActor);
}
