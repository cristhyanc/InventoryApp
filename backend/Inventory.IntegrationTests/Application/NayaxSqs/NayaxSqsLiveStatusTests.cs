using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// The stored half of the live decision of issue #1106, against a real database: what the Dashboard's
/// synchronization reads from the business's own Nayax SQS sender row, whose row it reads, and what
/// survives a restart.
///
/// <para>The claims only a relational database can settle are the tenancy ones - the read is scoped
/// by the central query filter and by nothing this adapter wrote - and the durability one: the
/// reconciliation instant is a column, so it is still there after the process that wrote it is gone,
/// which is exactly why the in-memory reader health is the only thing a restarted API waits for.</para>
/// </summary>
public class NayaxSqsLiveStatusTests
{
    private const string SenderId = "AIDAFICTIONALSENDERAA";
    private const string OtherSenderId = "AIDAFICTIONALSENDERBB";

    private static readonly DateTime Now = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task It_reads_the_current_businesss_own_sender_status()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(
            NayaxSqsSaleFixture.BusinessA,
            SenderId,
            isEnabled: true,
            lastMessageReceivedAtUtc: Now.AddMinutes(-4),
            lastReconciliationSucceededAtUtc: Now.AddMinutes(-30));

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var status = await new EfNayaxSqsLiveStatusStore(db).FindCurrentAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.True(status!.SenderIdRecorded);
        Assert.True(status.IsEnabled);
        Assert.Equal(Now.AddMinutes(-4), status.LastMessageReceivedAtUtc);
        Assert.Equal(Now.AddMinutes(-30), status.LastReconciliationSucceededAtUtc);
    }

    /// <summary>
    /// <strong>The projection cannot carry the sender id.</strong> Which AWS IAM user sends a
    /// business's sales is the Super Admin's record and is readable on no member-facing surface, so
    /// the record this port answers with has four values and no fifth - a structural guarantee rather
    /// than a mapper being careful.
    /// </summary>
    [Fact]
    public void The_status_record_publishes_no_sender_id()
    {
        var properties = typeof(NayaxSqsLiveSenderStatus)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(NayaxSqsLiveSenderStatus.IsEnabled),
                nameof(NayaxSqsLiveSenderStatus.LastMessageReceivedAtUtc),
                nameof(NayaxSqsLiveSenderStatus.LastReconciliationSucceededAtUtc),
                nameof(NayaxSqsLiveSenderStatus.SenderIdRecorded),
            ],
            properties);
    }

    /// <summary>
    /// A business whose sender id a Super Admin's "Remove" cleared keeps its row, and that row says
    /// no sender is recorded - which the rule reads as <see cref="NayaxSqsNotLiveReason.NoSender"/>,
    /// the same answer as no row at all.
    /// </summary>
    [Fact]
    public async Task A_cleared_sender_id_reads_as_no_sender_recorded()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(
            NayaxSqsSaleFixture.BusinessA,
            senderId: null,
            isEnabled: false,
            lastReconciliationSucceededAtUtc: Now.AddMinutes(-30));

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var status = await new EfNayaxSqsLiveStatusStore(db).FindCurrentAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.False(status!.SenderIdRecorded);
        Assert.Equal(
            NayaxSqsNotLiveReason.NoSender,
            NayaxSqsLivePolicy.FindNotLiveReason(StateOf(status, pollUtc: Now.AddSeconds(-5)), Now));
    }

    [Fact]
    public async Task A_business_with_no_sender_row_reads_as_nothing_recorded()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);

        Assert.Null(await new EfNayaxSqsLiveStatusStore(db).FindCurrentAsync(CancellationToken.None));
    }

    /// <summary>
    /// The two-business claim: business A's Dashboard reads business A's row and never business B's,
    /// because the central filter is what finds it - there is no business id on this port for anybody
    /// to pass the wrong one into.
    /// </summary>
    [Fact]
    public async Task One_businesss_status_is_never_another_businesss_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(
            NayaxSqsSaleFixture.BusinessA,
            SenderId,
            isEnabled: true,
            lastReconciliationSucceededAtUtc: Now.AddMinutes(-30));
        await fixture.RecordSenderAsync(
            NayaxSqsSaleFixture.BusinessB,
            OtherSenderId,
            isEnabled: false,
            lastReconciliationSucceededAtUtc: null);

        await using (var a = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            var status = await new EfNayaxSqsLiveStatusStore(a).FindCurrentAsync(CancellationToken.None);
            Assert.True(status!.IsEnabled);
            Assert.Equal(Now.AddMinutes(-30), status.LastReconciliationSucceededAtUtc);
        }

        await using (var b = fixture.Context(NayaxSqsSaleFixture.BusinessB))
        {
            var status = await new EfNayaxSqsLiveStatusStore(b).FindCurrentAsync(CancellationToken.None);
            Assert.False(status!.IsEnabled);
            Assert.Null(status.LastReconciliationSucceededAtUtc);
        }
    }

    /// <summary>
    /// <strong>The restart case.</strong> The reconciliation instant is durable, so it is exactly
    /// where it was after the process that wrote it has gone; the reader health is not, so a restarted
    /// API is not live until its first successful poll and the Dashboard falls back to today's sync in
    /// the meantime. Both halves are asserted here together, because together they are the claim.
    /// </summary>
    [Fact]
    public async Task A_restart_keeps_the_reconciliation_instant_and_waits_for_the_first_poll()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderId);

        // The run that fully succeeded, in the process that is about to be restarted.
        await using (var run = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            await new Inventory.Infrastructure.Persistence.EfNayaxReconciliationSuccessStore(run)
                .RecordSucceededAsync(Now.AddMinutes(-20), CancellationToken.None);
        }

        // The new process: a new context, and a health record with nothing on it at all.
        var health = new NayaxSqsReaderHealth();
        NayaxSqsLiveSenderStatus? status;
        await using (var restarted = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            status = await new EfNayaxSqsLiveStatusStore(restarted)
                .FindCurrentAsync(CancellationToken.None);
        }

        // The durable half survived.
        Assert.Equal(Now.AddMinutes(-20), status!.LastReconciliationSucceededAtUtc);

        // The in-memory half did not, which is a true statement rather than a recovered one.
        Assert.Equal(
            NayaxSqsNotLiveReason.ReaderNotPolling,
            NayaxSqsLivePolicy.FindNotLiveReason(StateOf(status, health.Current()), Now));

        health.RecordSuccessfulPoll(Now.AddSeconds(-5));

        Assert.Null(NayaxSqsLivePolicy.FindNotLiveReason(StateOf(status, health.Current()), Now));
    }

    /// <summary>
    /// And the instant the hourly run writes is the one the Dashboard reads: the two sides of issues
    /// #1104 and #1106 meet on this column, so a run that succeeds makes a business live without
    /// anything else being written.
    /// </summary>
    [Fact]
    public async Task A_successful_reconciliation_is_what_makes_a_business_live()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderId);

        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddSeconds(-5));

        await using (var before = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            var status = await new EfNayaxSqsLiveStatusStore(before)
                .FindCurrentAsync(CancellationToken.None);

            Assert.Equal(
                NayaxSqsNotLiveReason.ReconciliationStale,
                NayaxSqsLivePolicy.FindNotLiveReason(StateOf(status!, health.Current()), Now));
        }

        await using (var run = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            await new EfNayaxReconciliationSuccessStore(run)
                .RecordSucceededAsync(Now.AddMinutes(-1), CancellationToken.None);
        }

        await using (var after = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            var status = await new EfNayaxSqsLiveStatusStore(after)
                .FindCurrentAsync(CancellationToken.None);

            Assert.Null(NayaxSqsLivePolicy.FindNotLiveReason(StateOf(status!, health.Current()), Now));
        }
    }

    /// <summary>
    /// The read changes nothing: it is the one path on this row that only looks, so a Dashboard load
    /// leaves the sender id, the flag, the revision and both instants exactly as they were.
    /// </summary>
    [Fact]
    public async Task Reading_the_status_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(
            NayaxSqsSaleFixture.BusinessA,
            SenderId,
            isEnabled: true,
            lastMessageReceivedAtUtc: Now.AddMinutes(-4),
            lastReconciliationSucceededAtUtc: Now.AddMinutes(-30));

        await using (var db = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            await new EfNayaxSqsLiveStatusStore(db).FindCurrentAsync(CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        var row = await read.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync();

        Assert.Equal(SenderId, row.AwsSenderId);
        Assert.True(row.IsEnabled);
        Assert.Equal(1, row.Revision);
        Assert.Equal(Now.AddMinutes(-4), row.LastMessageReceivedAtUtc);
        Assert.Equal(Now.AddMinutes(-30), row.LastReconciliationSucceededAtUtc);
    }

    /// <summary>The use case's own composition of the two halves, as these tests exercise it.</summary>
    private static NayaxSqsLiveState StateOf(
        NayaxSqsLiveSenderStatus status, NayaxSqsReaderHealthReading health) =>
        new(
            status.SenderIdRecorded,
            status.IsEnabled,
            health.LastSuccessfulPollUtc,
            health.ConsecutivePollFailures,
            status.LastReconciliationSucceededAtUtc);

    /// <summary>The same, for a case that only needs a successful poll at a given instant.</summary>
    private static NayaxSqsLiveState StateOf(NayaxSqsLiveSenderStatus status, DateTime pollUtc)
    {
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(pollUtc);
        return StateOf(status, health.Current());
    }
}
