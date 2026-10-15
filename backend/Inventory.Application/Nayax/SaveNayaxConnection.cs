using System.Text.Json;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Nayax;

/// <summary>
/// What a member may be told about their business's Nayax connection (issue #537): how usable it is,
/// and whether a re-test is still needed to verify the machine checks.
///
/// It is deliberately these two fields and nothing else. The banners of #539 are shown to every
/// member, and the operator id, the credential revision, the last-tested instant and the individual
/// check lines are all configuration detail that only the Owner who manages the integration has any
/// use for.
/// </summary>
/// <param name="Status">How usable the stored credentials currently are.</param>
/// <param name="MachineChecksPending">
/// Whether the last completed check left the machine-scoped checks unverified, because the business
/// had no machine in Nayax to run them against.
/// </param>
public sealed record NayaxConnectionStatusSummary(
    NayaxConnectionStatus Status,
    bool MachineChecksPending)
{
    /// <summary>The summary of a business that has stored no connection at all.</summary>
    public static NayaxConnectionStatusSummary NotConfigured { get; } =
        new(NayaxConnectionStatus.NotConfigured, MachineChecksPending: false);

    /// <summary>The summary of <paramref name="connection"/>, or of no connection at all.</summary>
    public static NayaxConnectionStatusSummary From(NayaxConnection? connection) =>
        connection is null
            ? NotConfigured
            : new NayaxConnectionStatusSummary(connection.Status, connection.MachineChecksPending);
}

/// <summary>
/// Promotes one completed connection test into the business's Nayax connection (issue #537, a slice
/// of #506 under #328).
///
/// <para><strong>It accepts a draft id and never a token.</strong> That is the whole point of the
/// two-step shape: the credentials being stored are exactly the ones the server tested, so a
/// successful test cannot be presented as evidence for a different token, and a request cannot store
/// a credential nothing has ever tried. The operator id, the token and the results all come from the
/// claimed draft; the request carries one opaque identifier.</para>
///
/// <para><strong>Claiming the draft and storing the credential are one operation.</strong> The claim
/// is a conditional delete that must remove exactly one row
/// (<see cref="INayaxConnectionTestDraftStore.TryClaimAsync"/>), and it runs inside the same
/// transaction as the credential write. Two simultaneous saves of one draft therefore end as one
/// save and one refusal, with the credential revision increased exactly once, and a save that cannot
/// complete puts the draft back instead of destroying a test result that was never accepted.</para>
///
/// <para><strong>The status comes from that draft's own results</strong>, through the one Domain rule
/// (<see cref="NayaxConnectionCheckOutcomePolicy"/>), and a run whose sign-in did not pass is
/// refused rather than stored: a token that has never been shown to authenticate must not replace
/// whatever the business is using today. Machine lines that were never checked do not hold
/// <see cref="NayaxConnectionStatus.Ready"/> back - a business with no machines yet has to be able to
/// finish connecting - and the saved connection records that those checks are still pending so the
/// Owner can be asked to re-test once machines exist.</para>
///
/// <para><strong>Nothing about the owner comes from the request.</strong> The business is resolved
/// from the authenticated actor's membership and the actor from the validated Entra
/// <c>(tid, oid)</c> pair; the draft must belong to both, so neither another business nor another
/// member of the same business can save it.</para>
/// </summary>
public sealed class SaveNayaxConnection
{
    /// <summary>
    /// The one answer to every way a draft can be unclaimable - unknown, expired, another
    /// business's, another member's, or already saved. They are deliberately indistinguishable:
    /// telling a caller which would tell them that somebody else's draft exists, and the action is
    /// the same in all of them.
    /// </summary>
    public const string DraftUnavailableMessage =
        "This test result was already used or has expired. Run the test again.";

    /// <summary>
    /// Why a tested pair was not stored. It names no part of the submitted token and repeats no
    /// Nayax response: the test result the Owner is looking at already says which line failed.
    /// </summary>
    public const string SignInDidNotPassMessage =
        "The tested token did not sign in to Nayax, so it was not saved. Fix what the test reported "
            + "and run the test again.";

    private readonly INayaxConnectionTestDraftStore _drafts;
    private readonly INayaxConnectionStore _connections;
    private readonly INayaxConnectionSaveTransactions _transactions;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public SaveNayaxConnection(
        INayaxConnectionTestDraftStore drafts,
        INayaxConnectionStore connections,
        INayaxConnectionSaveTransactions transactions,
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _drafts = drafts;
        _connections = connections;
        _transactions = transactions;
        _currentBusiness = currentBusiness;
        _actors = actors;
        _clock = clock;
    }

    /// <param name="draftId">The opaque identifier a completed test returned.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="DomainValidationException">
    /// No draft id was submitted, so there is nothing to save. Refused before anything is read.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// There was no such draft to claim; see <see cref="DraftUnavailableMessage"/>.
    /// </exception>
    /// <exception cref="DomainValidationException">
    /// The claimed draft's sign-in check did not pass, so the credentials were not stored; see
    /// <see cref="SignInDidNotPassMessage"/>.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no resolvable business, or no identity a draft could be bound to.
    /// </exception>
    public async Task<NayaxConnectionStatusSummary> Handle(
        Guid? draftId,
        CancellationToken cancellationToken = default)
    {
        if (draftId is not { } requestedDraftId || requestedDraftId == Guid.Empty)
        {
            // Refused before a business is resolved or a row is touched: a request with no draft id
            // names no test, and the all-zero GUID is no test's id either.
            throw new DomainValidationException(
                "Run the connection test first: saving needs the draft id a completed test returned.");
        }

        // Resolved before anything is read or written, and never from the request.
        _ = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);
        var actor = RequireActor();

        // Outside the transaction below, deliberately (issue #681). An expired draft is already
        // unclaimable, so this changes nothing about what this save may do; what it does is stop a
        // credential ciphertext nobody can use any more from sitting in the database until the next
        // test happens to replace it. Swept here rather than inside the claim because the refusal
        // further down rolls that transaction back - which is precisely the case that leaves a draft
        // behind.
        await _drafts.DiscardExpiredAsync(cancellationToken);

        await using var transaction = await _transactions.BeginAsync(cancellationToken);

        var claimed = await _drafts.TryClaimAsync(requestedDraftId, actor, cancellationToken)
            ?? throw new DomainConflictException(DraftUnavailableMessage);

        var report = Deserialize(claimed.ResultsJson);
        var outcome = report.Outcome;

        if (!NayaxConnectionCheckOutcomePolicy.MayBeSaved(outcome))
        {
            // The transaction is left uncommitted, so the claim is undone: a refused save consumes
            // nothing, and the draft is still there until it expires or the next test replaces it.
            throw new DomainValidationException(SignInDidNotPassMessage);
        }

        var saved = await _connections.SaveCredentialAsync(
            claimed.Credential.OperatorId,
            claimed.Credential.AccessToken,
            cancellationToken);

        // The status this draft's own results concluded, written to the revision this save just
        // created rather than to one some other caller may have written since.
        var status = NayaxConnectionCheckOutcomePolicy.ConcludedStatus(outcome)
            ?? throw new InvalidOperationException(
                "A savable connection check outcome concluded no status, which the Domain rule "
                    + "does not allow. The save is abandoned rather than storing a guessed status.");

        var applied = await _connections.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, status, _clock.UtcNow)
            {
                MachineChecksPending = report.MachineChecksPending,
            },
            cancellationToken);

        if (!applied)
        {
            // Unreachable: the revision was produced by the write immediately above, inside this
            // transaction. It is still checked, because the alternative to failing here is
            // committing a stored credential whose status is whatever the previous token's was.
            throw new InvalidOperationException(
                "The Nayax connection status could not be applied to the revision this save just "
                    + "stored, so the save is rolled back rather than leaving a credential beside a "
                    + "status that was not produced for it.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new NayaxConnectionStatusSummary(status, report.MachineChecksPending);
    }

    /// <summary>
    /// Reads back the report this server serialized when the test ran. A stored report that cannot
    /// be read is a failure, not an empty result: concluding a status from nothing would store a
    /// credential that no test has actually vouched for.
    /// </summary>
    private static NayaxConnectionCheckReport Deserialize(string resultsJson) =>
        JsonSerializer.Deserialize<NayaxConnectionCheckReport>(resultsJson)
            ?? throw new InvalidOperationException(
                "The stored Nayax connection test results could not be read, so no status can be "
                    + "concluded from them and nothing is saved.");

    /// <summary>
    /// The draft is bound to the Owner who created it, so a caller this application cannot identify
    /// has nothing to claim. Denying is the fail-closed answer.
    /// </summary>
    private ActorIdentity RequireActor() =>
        _actors.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.UnidentifiableActor);
}
