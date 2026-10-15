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
/// The Owner-only connection test of issue #536, over fake ports.
///
/// Four properties are what this use case is responsible for, and each is tested here rather than
/// through HTTP: the credentials it sends to Nayax are exactly the submitted ones, the result and
/// the stored report never carry the token, the draft is bound to the resolved business's Owner and
/// expires, and the rate limit is spent before any Nayax call is made.
/// </summary>
public class TestNayaxConnectionTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";
    private const int BusinessId = 7;
    private static readonly DateTime Now = new(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);

    private static readonly ActorIdentity Owner = Actor("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task The_checks_run_with_the_submitted_operator_id_and_token()
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out _, out _);

        await test.Handle(OperatorId, Token);

        var candidate = Assert.Single(runner.Candidates);
        Assert.Equal(OperatorId, candidate.OperatorId);
        Assert.Equal(Token, candidate.AccessToken);
    }

    /// <summary>
    /// A pasted credential arrives with whatever whitespace the clipboard brought. Only the outer
    /// whitespace is removed, and what is sent and stored is the trimmed value - so a token that
    /// works is not reported as invalid because of a trailing newline.
    /// </summary>
    [Fact]
    public async Task Surrounding_whitespace_on_a_pasted_credential_is_trimmed()
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out var drafts, out _);

        await test.Handle($"  {OperatorId}\n", $"\t{Token}  \r\n");

        var candidate = Assert.Single(runner.Candidates);
        Assert.Equal(OperatorId, candidate.OperatorId);
        Assert.Equal(Token, candidate.AccessToken);
        Assert.Equal(Token, drafts.Current!.Credential.AccessToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_operator_id_is_refused_without_calling_nayax(string? operatorId)
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out var drafts, out _);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => test.Handle(operatorId, Token));

        Assert.Contains("operator id", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Candidates);
        Assert.Null(drafts.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_token_is_refused_without_calling_nayax(string? token)
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out var drafts, out _);

        await Assert.ThrowsAsync<DomainValidationException>(() => test.Handle(OperatorId, token));

        Assert.Empty(runner.Candidates);
        Assert.Null(drafts.Current);
    }

    /// <summary>
    /// Issue #681: an operator id that is not a number is refused before any Nayax call and before
    /// a rate-limit allowance is spent.
    ///
    /// <para>Nayax's own contract declares the operator path parameter as an <c>int64</c>
    /// (<c>GET /v1/operators/{OperatorID}/products</c>), and the value is interpolated into a
    /// request path - so the <c>..</c> and <c>/</c> cases below are the ones with teeth: they do not
    /// merely fail, they address a <em>different</em> endpoint. Two of the checks conclude "that
    /// operator is not yours" from a <c>404</c>, so an id that reached an endpoint the token
    /// <em>can</em> read would make that conclusion wrong for an operator the token does not
    /// cover.</para>
    /// </summary>
    [Theory]
    [InlineData("2002736764/../devices")]
    [InlineData("../../devices")]
    [InlineData("2002736764/products")]
    [InlineData("2002736764?x=1")]
    [InlineData("2002736764#fragment")]
    [InlineData("not-a-number")]
    [InlineData("-2002736764")]
    [InlineData("2002736764.0")]
    [InlineData("0")]
    [InlineData("00000")]
    [InlineData("12345678901234567890")]
    public async Task An_operator_id_that_is_not_a_number_is_refused_without_calling_nayax(string operatorId)
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out var drafts, out _);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => test.Handle(operatorId, Token));

        Assert.Equal(NayaxOperatorId.InvalidMessage, exception.Message);
        Assert.Empty(runner.Candidates);
        Assert.Null(drafts.Current);
        // The message tells the Owner where to find the right value and echoes nothing they typed.
        Assert.DoesNotContain(operatorId, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast: the operator ids Nayax actually issues are accepted unchanged, so the rule
    /// above refuses a crafted value rather than a long one.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("2002736764")]
    [InlineData("9223372036854775807")]
    public async Task A_numeric_operator_id_is_accepted(string operatorId)
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out _, out _);

        await test.Handle(operatorId, Token);

        Assert.Equal(operatorId, Assert.Single(runner.Candidates).OperatorId);
    }

    /// <summary>
    /// Issue #681: an expired draft is discarded before the Nayax calls, so a credential ciphertext
    /// nobody can use any more does not sit in the database waiting for the next successful test to
    /// replace it.
    ///
    /// <para>The run that matters is a <em>failing</em> one: the replacement at the end of a
    /// successful test already clears whatever was there, so the case that left a draft behind was a
    /// test Nayax never answered.</para>
    /// </summary>
    [Fact]
    public async Task An_expired_draft_is_discarded_even_when_the_test_itself_fails()
    {
        var clock = new FakeClock(Now);
        var drafts = new FakeNayaxConnectionTestDraftStore(clock);
        var failing = new FakeNayaxConnectionCheckRunner
        {
            Failure = new InvalidOperationException("Nayax never answered."),
        };

        // A draft from an earlier test, which has since expired.
        await drafts.ReplaceAsync(
            new NewNayaxConnectionTestDraft(
                Guid.NewGuid(),
                new NayaxConnectionCredential(OperatorId, Token, credentialRevision: 0),
                "{}",
                Owner,
                Now.AddMinutes(-30),
                Now.AddMinutes(-15)),
            CancellationToken.None);
        Assert.NotNull(drafts.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(failing, drafts, clock).Handle(OperatorId, Token));

        Assert.Equal(1, drafts.ExpiredDiscardCalls);
        Assert.True(drafts.DiscardedAnExpiredDraft);
        Assert.Null(drafts.Current);
    }

    /// <summary>
    /// A draft that has <em>not</em> expired is left alone by the sweep: it is still claimable, and
    /// discarding it would throw away a test result the Owner is about to save.
    /// </summary>
    [Fact]
    public async Task A_live_draft_is_not_discarded_by_the_sweep()
    {
        var clock = new FakeClock(Now);
        var drafts = new FakeNayaxConnectionTestDraftStore(clock);
        var failing = new FakeNayaxConnectionCheckRunner
        {
            Failure = new InvalidOperationException("Nayax never answered."),
        };
        var liveDraftId = Guid.NewGuid();

        await drafts.ReplaceAsync(
            new NewNayaxConnectionTestDraft(
                liveDraftId,
                new NayaxConnectionCredential(OperatorId, Token, credentialRevision: 0),
                "{}",
                Owner,
                Now,
                Now.AddMinutes(15)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(failing, drafts, clock).Handle(OperatorId, Token));

        Assert.False(drafts.DiscardedAnExpiredDraft);
        Assert.Equal(liveDraftId, drafts.Current!.Id);
    }

    /// <summary>
    /// A refusal names the field and never any part of the submitted value, because the message is
    /// returned to the caller verbatim.
    /// </summary>
    [Fact]
    public async Task A_refusal_message_never_carries_the_submitted_token()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out _, out _);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => test.Handle(string.Empty, Token));

        Assert.DoesNotContain(Token, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_result_reports_every_check_line_the_runner_produced()
    {
        var report = new NayaxConnectionCheckReport(
            [
                NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.ProductCatalogue,
                    NayaxConnectionCheckFailure.PermissionNotGranted),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastAlerts),
            ],
            0);
        var test = Create(new FakeNayaxConnectionCheckRunner(report), out _, out _);

        var outcome = await test.Handle(OperatorId, Token);

        Assert.Same(report, outcome.Result!.Report);
        Assert.False(outcome.Result.Report.NothingFailed);
    }

    /// <summary>
    /// The result the Owner gets back must be usable without ever repeating the secret they typed:
    /// an opaque draft id, an expiry and the lines. Rendering the whole result - which is what a
    /// response body and a log line both do - must not produce the token anywhere.
    /// </summary>
    [Fact]
    public async Task The_result_carries_no_token_anywhere_in_it()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out _, out _);

        var outcome = await test.Handle(OperatorId, Token);

        var rendered = JsonSerializer.Serialize(outcome) + '\n' + outcome.ToString();
        Assert.DoesNotContain(Token, rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stored results are the report and nothing else. They live beside the ciphertext, so a
    /// token serialized into them would be a plaintext copy of the very secret the column next to
    /// them is encrypted to protect.
    /// </summary>
    [Fact]
    public async Task The_stored_results_carry_no_token()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out var drafts, out _);

        await test.Handle(OperatorId, Token);

        Assert.DoesNotContain(Token, drafts.Current!.ResultsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_draft_is_stored_with_the_tested_credentials_and_the_owner_who_tested_them()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out var drafts, out _);

        var outcome = await test.Handle(OperatorId, Token);

        var draft = Assert.Single(drafts.Stored);
        Assert.Equal(outcome.Result!.DraftId, draft.Id);
        Assert.Equal(OperatorId, draft.Credential.OperatorId);
        Assert.Equal(Token, draft.Credential.AccessToken);
        Assert.Equal(Owner, draft.CreatedBy);
        Assert.Equal(Now, draft.CreatedAtUtc);
    }

    [Fact]
    public async Task The_draft_expires_fifteen_minutes_after_the_test()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out var drafts, out _);

        var outcome = await test.Handle(OperatorId, Token);

        Assert.Equal(TimeSpan.FromMinutes(15), TestNayaxConnection.DraftLifetime);
        Assert.Equal(Now.AddMinutes(15), outcome.Result!.ExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(15), drafts.Current!.ExpiresAtUtc);
    }

    /// <summary>
    /// The identifier is opaque: a random value carrying nothing about the business, the actor or
    /// the credentials, and a different one every test - so a draft id cannot be guessed from an
    /// earlier one.
    /// </summary>
    [Fact]
    public async Task Each_test_returns_its_own_unguessable_draft_id()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out _, out _);

        var first = await test.Handle(OperatorId, Token);
        var second = await test.Handle(OperatorId, Token);

        Assert.NotEqual(first.Result!.DraftId, second.Result!.DraftId);
        Assert.NotEqual(Guid.Empty, first.Result.DraftId);
    }

    /// <summary>
    /// One draft per business: a new test replaces the previous one, so the draft id the first test
    /// returned stops existing rather than both staying usable.
    /// </summary>
    [Fact]
    public async Task A_new_test_replaces_the_previous_draft()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out var drafts, out _);

        var first = await test.Handle(OperatorId, Token);
        var second = await test.Handle(OperatorId, "another-fake-token");

        Assert.Equal(2, drafts.Stored.Count);
        Assert.Equal(second.Result!.DraftId, drafts.Current!.Id);
        Assert.Null(await drafts.FindAsync(first.Result!.DraftId, Owner, CancellationToken.None));
        Assert.NotNull(await drafts.FindAsync(second.Result.DraftId, Owner, CancellationToken.None));
    }

    /// <summary>
    /// Past its expiry the draft stops existing, rather than staying readable until something
    /// happens to replace it.
    /// </summary>
    [Fact]
    public async Task An_expired_draft_no_longer_exists()
    {
        var clock = new FakeClock(Now);
        var drafts = new FakeNayaxConnectionTestDraftStore(clock);
        var test = Create(new FakeNayaxConnectionCheckRunner(), drafts, clock);

        var outcome = await test.Handle(OperatorId, Token);

        clock.UtcNow = Now.AddMinutes(14);
        Assert.NotNull(await drafts.FindAsync(outcome.Result!.DraftId, Owner, CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(15);
        Assert.Null(await drafts.FindAsync(outcome.Result.DraftId, Owner, CancellationToken.None));
    }

    /// <summary>
    /// The draft belongs to the Owner who created it. Another identity naming the same id is told
    /// the draft does not exist, which is also what a caller from another business is told by the
    /// tenant filters.
    /// </summary>
    [Fact]
    public async Task Another_actor_cannot_pick_up_the_draft()
    {
        var test = Create(new FakeNayaxConnectionCheckRunner(), out var drafts, out _);

        var outcome = await test.Handle(OperatorId, Token);

        var someoneElse = Actor("22222222-2222-2222-2222-222222222222");
        Assert.Null(await drafts.FindAsync(outcome.Result!.DraftId, someoneElse, CancellationToken.None));
    }

    /// <summary>
    /// The rate limit is spent before the runner is reached, so a refused request makes no Nayax
    /// call and stores nothing - it does not even replace the draft the previous test left.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_request_calls_nayax_not_at_all_and_stores_nothing()
    {
        var runner = new FakeNayaxConnectionCheckRunner();
        var test = Create(runner, out var drafts, out _);

        for (var run = 0; run < NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.NotNull((await test.Handle(OperatorId, Token)).Result);
        }

        var allowedRuns = runner.Candidates.Count;
        var draftsStored = drafts.Stored.Count;

        var refused = await test.Handle(OperatorId, Token);

        Assert.Null(refused.Result);
        Assert.NotNull(refused.RetryAfter);
        Assert.True(refused.RetryAfter > TimeSpan.Zero);
        Assert.Equal(allowedRuns, runner.Candidates.Count);
        Assert.Equal(draftsStored, drafts.Stored.Count);
    }

    /// <summary>
    /// A caller whose business does not resolve tests nothing: the business is resolved before the
    /// rate limit is claimed and before any Nayax call, so there is no allowance to spend and no
    /// draft to bind.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_resolvable_business_tests_nothing()
    {
        var clock = new FakeClock(Now);
        var runner = new FakeNayaxConnectionCheckRunner();
        var drafts = new FakeNayaxConnectionTestDraftStore(clock);
        var test = new TestNayaxConnection(
            runner,
            drafts,
            new NayaxConnectionTestRateLimiter(clock),
            FakeCurrentBusinessProvider.Denied(),
            FakeAuthenticatedActorAccessor.Identified(Owner),
            clock);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => test.Handle(OperatorId, Token));

        Assert.Empty(runner.Candidates);
        Assert.Null(drafts.Current);
    }

    /// <summary>
    /// A draft has to be bound to an identity, so a caller this application cannot identify is
    /// denied rather than having a token stored against nobody.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_usable_identity_tests_nothing()
    {
        var clock = new FakeClock(Now);
        var runner = new FakeNayaxConnectionCheckRunner();
        var drafts = new FakeNayaxConnectionTestDraftStore(clock);
        var test = new TestNayaxConnection(
            runner,
            drafts,
            new NayaxConnectionTestRateLimiter(clock),
            new FakeCurrentBusinessProvider(BusinessId),
            FakeAuthenticatedActorAccessor.Unidentifiable(),
            clock);

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => test.Handle(OperatorId, Token));

        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, exception.Reason);
        Assert.Empty(runner.Candidates);
        Assert.Null(drafts.Current);
    }

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate("99999999-9999-9999-9999-999999999999", objectId, out var actor));
        return actor!;
    }

    private static TestNayaxConnection Create(
        FakeNayaxConnectionCheckRunner runner,
        out FakeNayaxConnectionTestDraftStore drafts,
        out FakeClock clock)
    {
        clock = new FakeClock(Now);
        drafts = new FakeNayaxConnectionTestDraftStore(clock);
        return Create(runner, drafts, clock);
    }

    private static TestNayaxConnection Create(
        FakeNayaxConnectionCheckRunner runner,
        FakeNayaxConnectionTestDraftStore drafts,
        FakeClock clock) =>
        new(
            runner,
            drafts,
            new NayaxConnectionTestRateLimiter(clock),
            new FakeCurrentBusinessProvider(BusinessId),
            FakeAuthenticatedActorAccessor.Identified(Owner),
            clock);
}
