using Inventory.Application.PlatformNayaxSqs;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Super Admin's Nayax SQS sender decision of issue #1101 under real concurrency, on two
/// independent connections to one real SQLite file through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para>The race the issue names is two <c>PUT</c>s presenting the same revision. That is a
/// check-then-write over the same row - "is the stored revision still 1?" then "write revision 2" -
/// and a check-then-write that is not serialised is not a check: without the
/// <c>BEGIN IMMEDIATE</c> transaction and the re-read inside it, both would pass and the second
/// write would silently replace the first administrator's decision about whose sales a queue
/// message is.</para>
///
/// <para>The whole use case is exercised, not just the adapter: the real
/// <see cref="ManageBusinessNayaxSqsSender"/> over the real
/// <see cref="EfPlatformNayaxSqsSenderStore"/> and the migrated schema, with only the authenticated
/// actor, the clock and the log sink faked - because those are the things a test has to decide
/// rather than observe.</para>
///
/// <para>What a test here may assert is what the rules guarantee, and not which decision won. Both
/// outcomes are correct answers to two administrators pressing a button at once, so the assertion is
/// the state the database is left in and the one refusal that is allowed; an assertion that named the
/// winner would pass on a quiet machine and fail under load without anything being wrong.</para>
/// </summary>
public class NayaxSqsSenderConcurrencyTests : IAsyncDisposable
{
    private const int BusinessId = 1;
    private const int OtherBusinessId = 2;
    private const string AdministratorOid = "99999999-9999-9999-9999-999999999999";
    private const string FirstSenderId = "AIDAEXAMPLEUSERID111";
    private const string SecondSenderId = "AIDAEXAMPLEUSERID222";

    private static readonly DateTime Now = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(BusinessId, OtherBusinessId);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// Two first recordings arriving at once: exactly one row is created, the other attempt is
    /// refused, and the row that exists is at revision 1 rather than 2.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_first_recordings_create_one_row()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => SetAsync(FirstSenderId, expectedRevision: null, token),
            token => SetAsync(SecondSenderId, expectedRevision: null, token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(NayaxSqsSenderPolicy.StaleRevisionMessage, conflict.Message);

        var sender = Assert.Single(Senders());
        Assert.Equal(BusinessId, sender.BusinessId);
        Assert.Equal(1, sender.Revision);
        Assert.Contains(sender.AwsSenderId, (string?[])[FirstSenderId, SecondSenderId]);

        // One decision, one audit row. A trail with two entries at revision 1 would mean the losing
        // write committed its evidence without its change.
        Assert.Single(Audits());
    }

    /// <summary>
    /// The race the issue asks for: two changes presenting the same revision. One is recorded, the
    /// other is refused with the one "reload it and try again" sentence, and the row is left at
    /// revision 2 - never 3, which is what a lost update would look like.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_changes_at_the_same_revision_record_one_decision()
    {
        await SetAsync(FirstSenderId, expectedRevision: null, CancellationToken.None);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => SetAsync(SecondSenderId, expectedRevision: 1, token),
            token => SetAsync(awsSenderId: null, expectedRevision: 1, token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(NayaxSqsSenderPolicy.StaleRevisionMessage, conflict.Message);

        var sender = Assert.Single(Senders());
        Assert.Equal(2, sender.Revision);

        if (sender.AwsSenderId is null)
        {
            // The removal won: the mapping is off and the id it had is free again.
            Assert.False(sender.IsEnabled);
        }
        else
        {
            // The change won: the new id is recorded and the mapping stays on.
            Assert.Equal(SecondSenderId, sender.AwsSenderId);
            Assert.True(sender.IsEnabled);
        }

        // Two audit rows in total - the create and whichever change committed - and never three.
        var trail = Audits();
        Assert.Equal(2, trail.Count);
        Assert.Equal([1, 2], trail.Select(row => row.Revision).ToArray());
    }

    /// <summary>
    /// And two businesses recording the <em>same</em> sender id at once: the global unique index is
    /// what resolves it, so exactly one business ends up holding the id and the other is refused with
    /// the duplicate sentence rather than both appearing to own it.
    /// </summary>
    [Fact]
    public async Task Two_businesses_recording_one_sender_id_at_once_leave_it_with_one_of_them()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => SetAsync(FirstSenderId, expectedRevision: null, token, BusinessId),
            token => SetAsync(FirstSenderId, expectedRevision: null, token, OtherBusinessId));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(NayaxSqsSenderPolicy.DuplicateSenderIdMessage, conflict.Message);

        // Exactly one row holds it, and the refused business has no row at all - its transaction
        // rolled the sender row and its audit row back together.
        var sender = Assert.Single(Senders());
        Assert.Equal(FirstSenderId, sender.AwsSenderId);
        Assert.Equal(1, sender.Revision);

        var audit = Assert.Single(Audits());
        Assert.Equal(sender.BusinessId, audit.BusinessId);
    }

    private Task SetAsync(
        string? awsSenderId,
        int? expectedRevision,
        CancellationToken cancellationToken,
        int businessId = BusinessId)
    {
        var db = TestAppDbContext.Denied(_harness.Options);

        var subject = new ManageBusinessNayaxSqsSender(
            FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(AdministratorOid)),
            new EfPlatformNayaxSqsSenderStore(db),
            new NoOpAudit(),
            new Inventory.Application.NayaxSqs.NayaxSqsReaderHealth(),
            new FakeClock(Now));

        return RunAndDisposeAsync();

        async Task RunAndDisposeAsync()
        {
            await using (db)
            {
                await subject.SetAsync(
                    businessId, awsSenderId, isEnabled: awsSenderId is not null, expectedRevision,
                    cancellationToken);
            }
        }
    }

    private List<BusinessNayaxSqsSender> Senders()
    {
        using var db = TestAppDbContext.Unrestricted(_harness.Options);
        return [.. db.BusinessNayaxSqsSenders.AsNoTracking().OrderBy(row => row.Id)];
    }

    private List<BusinessNayaxSqsSenderAudit> Audits()
    {
        using var db = TestAppDbContext.Unrestricted(_harness.Options);
        return [.. db.BusinessNayaxSqsSenderAudits.AsNoTracking().OrderBy(row => row.Id)];
    }

    /// <summary>
    /// The log sink is not what this test is about: the committed audit <em>row</em> is the evidence
    /// a race has to leave exactly one of, and that is read from the database above.
    /// </summary>
    private sealed class NoOpAudit : IPlatformNayaxSqsSenderAudit
    {
        public void Record(PlatformNayaxSqsSenderAuditEntry entry)
        {
        }
    }
}
