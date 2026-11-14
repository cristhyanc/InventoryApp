using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// Promoting a tested draft into the business's connection (issue #537), over fake ports.
///
/// What this use case is responsible for is tested here rather than through HTTP: that the stored
/// credentials are the draft's own, that the status comes from that draft's own results, that a run
/// whose machine list did not pass stores nothing - including a draft written before the machine
/// list became the gate (issue #1310) - that a draft is consumed exactly once, and that the
/// claim and the write are inside one transaction that a refusal never commits. The relational
/// proofs - the conditional delete, two concurrent saves, and a refused save leaving the draft in
/// place - are in <c>Adapters/Persistence</c>.
/// </summary>
public class SaveNayaxConnectionTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";
    private const int BusinessId = 7;
    private static readonly DateTime Now = new(2026, 10, 12, 1, 15, 0, DateTimeKind.Utc);

    private static readonly ActorIdentity Owner = Actor("11111111-1111-1111-1111-111111111111");
    private static readonly ActorIdentity SomeoneElse = Actor("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_draft_whose_every_check_passed_is_saved_ready()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        var summary = await fixture.Save.Handle(draftId);

        Assert.Equal(new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, false), summary);
        Assert.Equal(NayaxConnectionStatus.Ready, fixture.Connections.Connection!.Status);
        Assert.Equal(1, fixture.Connections.Connection.CredentialRevision);
        Assert.Equal(Now, fixture.Connections.Connection.LastTestedAtUtc);
    }

    /// <summary>
    /// The credentials that land in the connection are the draft's own, which is the whole point of
    /// saving by draft id: an earlier token's successful test cannot be used to store a different
    /// one.
    /// </summary>
    [Fact]
    public async Task The_stored_credentials_are_the_tested_ones()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        await fixture.Save.Handle(draftId);

        Assert.Equal(OperatorId, fixture.Connections.Connection!.OperatorId);
        Assert.Equal(new[] { Token }, fixture.Connections.SavedTokens);
    }

    /// <summary>
    /// A token that reads the machine list but lacks another permission is a real credential with a
    /// gap, so it is stored and the status records the gap. Ordinary Nayax operations are still
    /// allowed in that state.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_machine_list_passed_beside_a_failure_is_saved_pending_permissions()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.Machines),
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.MachineLastSales,
                NayaxConnectionCheckFailure.PermissionNotGranted)));

        var summary = await fixture.Save.Handle(draftId);

        Assert.Equal(NayaxConnectionStatus.PendingPermissions, summary.Status);
        Assert.False(summary.MachineChecksPending);
    }

    /// <summary>
    /// A business with no machines in Nayax yet must be able to finish connecting: unchecked machine
    /// lines do not hold <c>Ready</c> back, and the saved connection records that those checks are
    /// still pending so the Owner can be asked to re-test later.
    /// </summary>
    [Fact]
    public async Task Unchecked_machine_lines_are_saved_ready_with_the_machine_checks_recorded_as_pending()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.Machines),
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.ProductCatalogue),
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.ProductGroups),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.Machine),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineProducts),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastSales),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastAlerts)));

        var summary = await fixture.Save.Handle(draftId);

        Assert.Equal(new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, true), summary);
        Assert.True(fixture.Connections.Connection!.MachineChecksPending);
    }

    /// <summary>
    /// A machine list that was refused is not "no machines yet" (issue #681), and since issue #1310
    /// it is not savable either: the machine list is the credential gate, so a token Nayax refused
    /// it to has not been shown to serve this business at all. Nothing is stored, and the pending
    /// machine checks are not recorded - the refusal is what needs fixing, not the machine setup.
    /// </summary>
    [Fact]
    public async Task A_refused_machine_list_is_not_saved_at_all()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.Machines,
                NayaxConnectionCheckFailure.PermissionNotGranted),
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.ProductCatalogue),
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.ProductGroups),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.Machine),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineProducts),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastSales),
            NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastAlerts)));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(SaveNayaxConnection.MachineListDidNotPassMessage, exception.Message);
        Assert.Null(fixture.Connections.Connection);
        Assert.Empty(fixture.Connections.SavedTokens);
        Assert.Equal(0, fixture.Transactions.Committed);
    }

    /// <summary>
    /// A token whose machine list did not pass has never been shown to serve this business, so it is
    /// not stored - and the transaction is not committed, which is what leaves the draft in place.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheckFailure.InvalidOrExpiredToken)]
    [InlineData(NayaxConnectionCheckFailure.NayaxUnavailable)]
    [InlineData(NayaxConnectionCheckFailure.UnexpectedResponse)]
    [InlineData(NayaxConnectionCheckFailure.PermissionNotGranted)]
    public async Task A_draft_whose_machine_list_did_not_pass_is_refused_and_stores_nothing(
        NayaxConnectionCheckFailure failure)
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Failed(NayaxConnectionCheck.Machines, failure)));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(SaveNayaxConnection.MachineListDidNotPassMessage, exception.Message);
        Assert.Null(fixture.Connections.Connection);
        Assert.Empty(fixture.Connections.SavedTokens);
        Assert.Equal(1, fixture.Transactions.Begun);
        Assert.Equal(0, fixture.Transactions.Committed);
        Assert.Equal(1, fixture.Transactions.RolledBack);
    }

    /// <summary>
    /// A draft is single-use. The second save of one id is refused with the fixed sentence, and the
    /// credential revision is increased exactly once - the property that makes two simultaneous
    /// saves safe.
    /// </summary>
    [Fact]
    public async Task A_draft_can_only_be_saved_once()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        await fixture.Save.Handle(draftId);
        var exception = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(SaveNayaxConnection.DraftUnavailableMessage, exception.Message);
        Assert.Equal(new[] { draftId }, fixture.Drafts.Claimed);
        Assert.Equal(1, fixture.Connections.Connection!.CredentialRevision);
        Assert.Equal(new[] { Token }, fixture.Connections.SavedTokens);
    }

    [Fact]
    public async Task An_expired_draft_is_refused()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());
        fixture.Clock.UtcNow = Now + TestNayaxConnection.DraftLifetime;

        var exception = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(SaveNayaxConnection.DraftUnavailableMessage, exception.Message);
        Assert.Null(fixture.Connections.Connection);
        Assert.Empty(fixture.Drafts.Claimed);
    }

    /// <summary>
    /// An id that is not this business's own draft, and a draft another member of the business
    /// created, are the same answer as an expired one: there is no such draft. Which it was is never
    /// reported.
    /// </summary>
    [Fact]
    public async Task An_unknown_draft_or_another_member_s_draft_is_refused_identically()
    {
        var fixture = new Fixture();
        fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        var unknown = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.Save.Handle(Guid.NewGuid()));

        var otherOwner = new Fixture(SomeoneElse);
        var theirDraftId = otherOwner.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed(), Owner);
        var otherMember = await Assert.ThrowsAsync<DomainConflictException>(
            () => otherOwner.Save.Handle(theirDraftId));

        Assert.Equal(SaveNayaxConnection.DraftUnavailableMessage, unknown.Message);
        Assert.Equal(SaveNayaxConnection.DraftUnavailableMessage, otherMember.Message);
        Assert.Empty(fixture.Connections.SavedTokens);
        Assert.Empty(otherOwner.Connections.SavedTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_request_with_no_usable_draft_id_is_refused_before_anything_is_read(string? draftId)
    {
        var fixture = new Fixture();
        fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        await Assert.ThrowsAsync<DomainValidationException>(
            () => fixture.Save.Handle(draftId is null ? null : (Guid?)Guid.Parse(draftId)));

        Assert.Equal(0, fixture.Transactions.Begun);
        Assert.Empty(fixture.Drafts.Claimed);
        Assert.Null(fixture.Connections.Connection);
    }

    /// <summary>
    /// Saving replaces whatever the business had: a second save increments the revision again, and
    /// the new status is this draft's own rather than the previous credential's.
    /// </summary>
    [Fact]
    public async Task Saving_supersedes_the_previous_credential()
    {
        var fixture = new Fixture();
        await fixture.Save.Handle(fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed()));

        var second = fixture.StoreDraft(
            Report(
                NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.Machines),
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.ProductCatalogue,
                    NayaxConnectionCheckFailure.PermissionNotGranted)),
            token: "a-second-fake-token");
        var summary = await fixture.Save.Handle(second);

        Assert.Equal(NayaxConnectionStatus.PendingPermissions, summary.Status);
        Assert.Equal(2, fixture.Connections.Connection!.CredentialRevision);
        Assert.Equal(new[] { Token, "a-second-fake-token" }, fixture.Connections.SavedTokens);
    }

    [Fact]
    public async Task A_caller_with_no_resolvable_business_saves_nothing()
    {
        var fixture = new Fixture(currentBusiness: FakeCurrentBusinessProvider.Denied());
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => fixture.Save.Handle(draftId));

        Assert.Equal(0, fixture.Transactions.Begun);
        Assert.Empty(fixture.Drafts.Claimed);
        Assert.Null(fixture.Connections.Connection);
    }

    /// <summary>
    /// A draft is bound to an identity, so a caller this application cannot identify has nothing to
    /// claim and is denied before the draft store is reached.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_usable_identity_saves_nothing()
    {
        var fixture = new Fixture(actors: FakeAuthenticatedActorAccessor.Unidentifiable());
        var draftId = fixture.StoreDraft(FakeNayaxConnectionCheckRunner.AllPassed());

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, exception.Reason);
        Assert.Equal(0, fixture.Transactions.Begun);
        Assert.Null(fixture.Connections.Connection);
    }

    /// <summary>
    /// A report with no machine-list line at all - which the runner never produces, but a stored
    /// string could hold - concludes nothing and is refused rather than saved on a guess.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_results_report_no_machine_list_is_refused()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.ProductCatalogue)));

        await Assert.ThrowsAsync<DomainValidationException>(() => fixture.Save.Handle(draftId));

        Assert.Null(fixture.Connections.Connection);
    }

    /// <summary>
    /// A draft still outstanding from before the machine list became the credential gate (issue
    /// #1310) is judged by the new rule. Its retired sign-in line - value <c>1</c>, the
    /// <c>GET devices</c> probe - is ignored: a passed one cannot promote a token whose machine list
    /// was refused, and a failed one cannot hold back a token whose machine list was read.
    /// </summary>
    [Fact]
    public async Task A_legacy_draft_cannot_be_saved_on_a_retired_sign_in_line()
    {
        var fixture = new Fixture();
        var draftId = fixture.StoreDraft(Report(
            NayaxConnectionCheckLine.Passed(RetiredSignIn),
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.Machines,
                NayaxConnectionCheckFailure.PermissionNotGranted)));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => fixture.Save.Handle(draftId));

        Assert.Equal(SaveNayaxConnection.MachineListDidNotPassMessage, exception.Message);
        Assert.Null(fixture.Connections.Connection);
    }

    /// <summary>
    /// The other half of the same rule, and the outcome the issue exists for: a legacy draft whose
    /// <c>GET devices</c> probe was refused for lack of the device-inventory permission, and whose
    /// machine list and every other declared check passed, saves as <c>Ready</c>. The retired line
    /// is not a failure to report, so it does not even record permissions as pending.
    /// </summary>
    [Fact]
    public async Task A_legacy_draft_refused_only_the_retired_devices_probe_is_saved_ready()
    {
        var fixture = new Fixture();
        var lines = new List<NayaxConnectionCheckLine>
        {
            NayaxConnectionCheckLine.Failed(RetiredSignIn, NayaxConnectionCheckFailure.PermissionNotGranted),
        };
        lines.AddRange(NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed));

        var draftId = fixture.StoreDraft(new NayaxConnectionCheckReport(lines, MachineCount: 1));

        var summary = await fixture.Save.Handle(draftId);

        Assert.Equal(new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, false), summary);
        Assert.Equal(NayaxConnectionStatus.Ready, fixture.Connections.Connection!.Status);
    }

    /// <summary>
    /// The retired <c>SignIn</c> check, as a stored report still names it: the number the enum
    /// reserved and no longer declares.
    /// </summary>
    private const NayaxConnectionCheck RetiredSignIn = (NayaxConnectionCheck)1;

    /// <summary>
    /// A report of exactly these lines. The machine count is irrelevant to a save - what decides is
    /// each line's own status - so it is left unknown.
    /// </summary>
    private static NayaxConnectionCheckReport Report(params NayaxConnectionCheckLine[] lines) =>
        new(lines, MachineCount: null);

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate("99999999-9999-9999-9999-999999999999", objectId, out var actor));
        return actor!;
    }

    /// <summary>The use case over fake ports, with the draft store seeded by the test itself.</summary>
    private sealed class Fixture
    {
        public Fixture(
            ActorIdentity? actor = null,
            FakeCurrentBusinessProvider? currentBusiness = null,
            FakeAuthenticatedActorAccessor? actors = null)
        {
            Actor = actor ?? Owner;
            Clock = new FakeClock(Now);
            Drafts = new FakeNayaxConnectionTestDraftStore(Clock);
            Connections = new FakeNayaxConnectionStore();
            Transactions = new FakeNayaxConnectionSaveTransactions();
            Save = new SaveNayaxConnection(
                Drafts,
                Connections,
                Transactions,
                currentBusiness ?? new FakeCurrentBusinessProvider(BusinessId),
                actors ?? FakeAuthenticatedActorAccessor.Identified(Actor),
                Clock);
        }

        public ActorIdentity Actor { get; }

        public FakeClock Clock { get; }

        public FakeNayaxConnectionTestDraftStore Drafts { get; }

        public FakeNayaxConnectionStore Connections { get; }

        public FakeNayaxConnectionSaveTransactions Transactions { get; }

        public SaveNayaxConnection Save { get; }

        /// <summary>
        /// Stores a draft the way a completed test does: the credential, the serialized report and
        /// the fifteen-minute expiry.
        /// </summary>
        public Guid StoreDraft(
            NayaxConnectionCheckReport report,
            ActorIdentity? createdBy = null,
            string token = Token)
        {
            var draftId = Guid.NewGuid();
            Drafts.ReplaceAsync(
                new NewNayaxConnectionTestDraft(
                    draftId,
                    new NayaxConnectionCredential(OperatorId, token, credentialRevision: 0),
                    JsonSerializer.Serialize(report),
                    createdBy ?? Actor,
                    Clock.UtcNow,
                    Clock.UtcNow + TestNayaxConnection.DraftLifetime),
                CancellationToken.None).GetAwaiter().GetResult();

            return draftId;
        }
    }
}
