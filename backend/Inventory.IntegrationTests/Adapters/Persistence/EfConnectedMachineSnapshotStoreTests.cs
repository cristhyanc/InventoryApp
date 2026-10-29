using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The connected-machine snapshot adapter of issue #978 against relational SQLite with the real
/// migration history.
///
/// <para>Relational rather than a model double, for the reason the other store tests in this folder
/// are: the daily claim <em>is</em> a unique index, append-only <em>is</em> a refused write, and "two
/// complete reads cannot produce two snapshots" is a claim about what a table actually contains.</para>
///
/// <para><strong>Two businesses throughout</strong> (AGENTS.md § Tenant ownership and data
/// isolation): every read and write is made through a context scoped to one of them, so a count
/// leaking across the boundary would fail a test rather than be invisible.</para>
/// </summary>
public class EfConnectedMachineSnapshotStoreTests : IAsyncLifetime
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime Today = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TakenAt = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = TestAppDbContext.Unrestricted(_options);
        await db.Database.MigrateAsync();

        db.Businesses.AddRange(
            NewBusiness(FirstBusinessId, "Vending Co"),
            NewBusiness(SecondBusinessId, "Second Vending Co"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// A recorded snapshot is stored with the day, the series, the count, the ids counted, the
    /// instant and the completeness of the read it came from - and the owning business is stamped
    /// centrally, from the scope, never from a parameter.
    /// </summary>
    [Fact]
    public async Task Records_the_days_snapshot_and_stamps_the_owning_business()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);

        var recorded = await new EfConnectedMachineSnapshotStore(db)
            .TryRecordAsync(Snapshot(Today, series: 1, 101, 102, 103), CancellationToken.None);

        Assert.True(recorded);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var row = await reader.BusinessConnectedMachineSnapshots.AsNoTracking().SingleAsync();

        Assert.Equal(FirstBusinessId, row.BusinessId);
        Assert.Equal(Today, row.SnapshotDateUtc);
        Assert.Equal(DateTimeKind.Utc, row.SnapshotDateUtc.Kind);
        Assert.Equal(1, row.AccountSeries);
        Assert.Equal(3, row.MachineCount);
        Assert.Equal("101,102,103", row.ProviderMachineIds);
        Assert.Equal(TakenAt, row.TakenAtUtc);
        Assert.True(row.ReadWasComplete);
    }

    /// <summary>
    /// The second complete read of a day claims nothing: the day and series are taken, so the second
    /// reader is told it did not write and the stored row keeps the count the first one recorded -
    /// no error, no retry, and no correction of an append-only row.
    /// </summary>
    [Fact]
    public async Task A_second_read_of_the_same_day_and_series_records_nothing()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        var store = new EfConnectedMachineSnapshotStore(db);

        Assert.True(await store.TryRecordAsync(
            Snapshot(Today, series: 1, 101, 102), CancellationToken.None));
        Assert.False(await store.TryRecordAsync(
            Snapshot(Today, series: 1, 101, 102, 103), CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var row = await reader.BusinessConnectedMachineSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.MachineCount);
    }

    /// <summary>
    /// The daily claim is the database's, not the adapter's: an insert that bypasses the
    /// check-then-insert is refused by the unique index on (business, UTC date, account series). That
    /// is what makes "concurrent page loads and the job can never produce two snapshots" true however
    /// the rows are written.
    /// </summary>
    [Fact]
    public async Task The_unique_constraint_refuses_a_second_row_for_one_business_day_and_series()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        await new EfConnectedMachineSnapshotStore(db)
            .TryRecordAsync(Snapshot(Today, series: 1, 101), CancellationToken.None);

        await using var direct = TestAppDbContext.Unrestricted(_options);
        direct.BusinessConnectedMachineSnapshots.Add(new BusinessConnectedMachineSnapshot
        {
            BusinessId = FirstBusinessId,
            SnapshotDateUtc = Today,
            AccountSeries = 1,
            MachineCount = 9,
            ProviderMachineIds = "201",
            TakenAtUtc = TakenAt,
            ReadWasComplete = true,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => direct.SaveChangesAsync());
    }

    /// <summary>
    /// A day the business's Nayax account changed on may hold one snapshot per series: the previous
    /// account's row stays exactly as it was, the new account records its own, and the latest-day read
    /// answers both so the Domain can take the later series as the day's count.
    /// </summary>
    [Fact]
    public async Task One_day_may_hold_one_snapshot_per_account_series()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        var store = new EfConnectedMachineSnapshotStore(db);

        Assert.True(await store.TryRecordAsync(
            Snapshot(Today, series: 1, 101, 102, 103), CancellationToken.None));
        Assert.True(await store.TryRecordAsync(
            Snapshot(Today, series: 2, 201), CancellationToken.None));

        var latestDay = await store.FindLatestDayAsync(CancellationToken.None);

        Assert.Equal(2, latestDay.Count);
        Assert.Equal(3, latestDay[0].MachineCount);
        Assert.Equal(1, latestDay[1].MachineCount);
        Assert.Equal(2, ConnectedMachineSnapshotPolicy.Latest(latestDay)!.AccountSeries);
    }

    /// <summary>
    /// The latest day is the latest <em>recorded</em> day, with that day's rows and no earlier one's:
    /// a count is as of a day, and an older snapshot must not be mistaken for it.
    /// </summary>
    [Fact]
    public async Task The_latest_day_read_answers_the_latest_recorded_days_rows()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        var store = new EfConnectedMachineSnapshotStore(db);

        await store.TryRecordAsync(Snapshot(Today.AddDays(-2), series: 1, 101), CancellationToken.None);
        await store.TryRecordAsync(Snapshot(Today.AddDays(-1), series: 1, 101, 102), CancellationToken.None);

        var latestDay = await store.FindLatestDayAsync(CancellationToken.None);

        var single = Assert.Single(latestDay);
        Assert.Equal(Today.AddDays(-1), single.DateUtc);
        Assert.Equal(2, single.MachineCount);
        Assert.Equal(new long[] { 101, 102 }, single.ProviderMachineIds);
    }

    /// <summary>A business with no snapshots has no count, which is not a count of zero.</summary>
    [Fact]
    public async Task A_business_with_no_snapshots_reads_nothing()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);

        Assert.Empty(await new EfConnectedMachineSnapshotStore(db).FindLatestDayAsync(CancellationToken.None));
    }

    /// <summary>
    /// One business's counts are invisible to another, and both may hold a snapshot for the same day
    /// and series: uniqueness is per business, because two businesses legitimately count their own
    /// fleets on the same day.
    /// </summary>
    [Fact]
    public async Task One_businesss_counts_are_invisible_to_another()
    {
        await using var first = TestAppDbContext.For(_options, FirstBusinessId);
        await using var second = TestAppDbContext.For(_options, SecondBusinessId);

        Assert.True(await new EfConnectedMachineSnapshotStore(first)
            .TryRecordAsync(Snapshot(Today, series: 1, 101, 102, 103), CancellationToken.None));
        Assert.True(await new EfConnectedMachineSnapshotStore(second)
            .TryRecordAsync(Snapshot(Today, series: 1, 201), CancellationToken.None));

        var secondsDay = await new EfConnectedMachineSnapshotStore(second)
            .FindLatestDayAsync(CancellationToken.None);

        Assert.Equal(1, Assert.Single(secondsDay).MachineCount);
        Assert.False(await new EfConnectedMachineSnapshotStore(second)
            .ExistsForAsync(Today.AddDays(-1), accountSeries: 1, CancellationToken.None));
        Assert.True(await new EfConnectedMachineSnapshotStore(second)
            .ExistsForAsync(Today, accountSeries: 1, CancellationToken.None));
    }

    /// <summary>
    /// A caller with no resolved business writes nothing: the central ownership enforcer refuses the
    /// insert, so a snapshot can never be recorded for a business nobody resolved.
    /// </summary>
    [Fact]
    public async Task A_denied_scope_records_nothing()
    {
        await using var db = TestAppDbContext.Denied(_options);

        await Assert.ThrowsAnyAsync<Exception>(() => new EfConnectedMachineSnapshotStore(db)
            .TryRecordAsync(Snapshot(Today, series: 1, 101), CancellationToken.None));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessConnectedMachineSnapshots.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A support session reads the viewed business's counts and writes none: the one read-only rule,
    /// applied at the persistence boundary rather than per endpoint.
    /// </summary>
    [Fact]
    public async Task A_support_session_reads_the_counts_and_writes_none()
    {
        await using var owner = TestAppDbContext.For(_options, FirstBusinessId);
        await new EfConnectedMachineSnapshotStore(owner)
            .TryRecordAsync(Snapshot(Today, series: 1, 101, 102), CancellationToken.None);

        await using var session = TestAppDbContext.SupportSession(_options, FirstBusinessId);
        var store = new EfConnectedMachineSnapshotStore(session);

        Assert.Equal(2, Assert.Single(await store.FindLatestDayAsync(CancellationToken.None)).MachineCount);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.TryRecordAsync(Snapshot(Today.AddDays(1), series: 1, 101), CancellationToken.None));
    }

    /// <summary>
    /// A stored snapshot cannot be changed. It is billing evidence: #969 charges from these rows, so a
    /// count that could be rewritten after it was charged would not be evidence, and the refusal is at
    /// the persistence boundary rather than merely absent from today's code paths.
    /// </summary>
    [Fact]
    public async Task A_stored_snapshot_cannot_be_changed()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        await new EfConnectedMachineSnapshotStore(db)
            .TryRecordAsync(Snapshot(Today, series: 1, 101, 102), CancellationToken.None);

        await using var editor = TestAppDbContext.For(_options, FirstBusinessId);
        var row = await editor.BusinessConnectedMachineSnapshots.SingleAsync();
        row.MachineCount = 99;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SaveChangesAsync());
        Assert.Contains("append-only", refusal.Message, StringComparison.Ordinal);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, (await reader.BusinessConnectedMachineSnapshots.AsNoTracking().SingleAsync()).MachineCount);
    }

    /// <summary>And it cannot be removed either: a later count is a later row.</summary>
    [Fact]
    public async Task A_stored_snapshot_cannot_be_deleted()
    {
        await using var db = TestAppDbContext.For(_options, FirstBusinessId);
        await new EfConnectedMachineSnapshotStore(db)
            .TryRecordAsync(Snapshot(Today, series: 1, 101, 102), CancellationToken.None);

        await using var remover = TestAppDbContext.For(_options, FirstBusinessId);
        remover.BusinessConnectedMachineSnapshots.Remove(
            await remover.BusinessConnectedMachineSnapshots.SingleAsync());

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => remover.SaveChangesAsync());
        Assert.Contains("append-only", refusal.Message, StringComparison.Ordinal);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Single(await reader.BusinessConnectedMachineSnapshots.AsNoTracking().ToListAsync());
    }

    private static ConnectedMachineSnapshot Snapshot(
        DateTime dayUtc,
        int series,
        params long[] providerMachineIds) =>
        new(
            dayUtc,
            series,
            providerMachineIds.Length,
            providerMachineIds,
            TakenAt,
            ReadWasComplete: true);

    private static Business NewBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        TimeZoneId = "Australia/Sydney",
        Status = BusinessStatus.Active,
        CreatedAtUtc = new DateTime(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc),
    };
}
