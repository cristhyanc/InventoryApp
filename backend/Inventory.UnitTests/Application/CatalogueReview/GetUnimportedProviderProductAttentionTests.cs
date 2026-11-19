using Inventory.Application.CatalogueReview;
using Inventory.Domain.Providers;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// What the Dashboard's unimported-provider-product use case decides on its own (issue #1352): the
/// window it asks the store for, how many products it asks to be named, and that it reads the clock
/// once.
///
/// <para>The store's own query - which sales qualify, the distinct count, the linked-product case and
/// tenant isolation - is asserted against a relational database in
/// <c>InventoryApi.Tests.Application.CatalogueReview.UnimportedProviderProductAttentionTests</c>, and
/// the Sydney/daylight-saving boundaries there too, because the real zoned calendar lives in
/// Infrastructure. Here the calendar is a fake, so what is pinned is the shape of the request this
/// use case makes.</para>
/// </summary>
public class GetUnimportedProviderProductAttentionTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 18, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task It_asks_for_the_thirty_business_date_window_ending_now()
    {
        var reader = new RecordingReader();
        var attention = new GetUnimportedProviderProductAttention(
            reader, new FakeClock(NowUtc), new FakeBusinessCalendar(NowUtc));

        await attention.Handle(CancellationToken.None);

        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), reader.FromUtc);
        Assert.Equal(NowUtc, reader.ToUtc);
    }

    /// <summary>
    /// The window's first instant is the start of the business day the policy names, not the
    /// business date itself: a business whose day starts at a non-UTC offset must not have its first
    /// day silently clipped.
    /// </summary>
    [Fact]
    public async Task The_window_starts_at_the_start_of_that_business_day()
    {
        var reader = new RecordingReader();
        var calendar = new OffsetBusinessCalendar(NowUtc, TimeSpan.FromHours(-11));
        var attention = new GetUnimportedProviderProductAttention(reader, new FakeClock(NowUtc), calendar);

        await attention.Handle(CancellationToken.None);

        Assert.Equal(new DateTime(2026, 10, 19, 13, 0, 0, DateTimeKind.Utc), reader.FromUtc);
    }

    [Fact]
    public async Task It_asks_for_at_most_the_policys_examples()
    {
        var reader = new RecordingReader();
        var attention = new GetUnimportedProviderProductAttention(
            reader, new FakeClock(NowUtc), new FakeBusinessCalendar(NowUtc));

        await attention.Handle(CancellationToken.None);

        Assert.Equal(UnimportedProviderProductAttentionPolicy.MaxExamples, reader.MaxExamples);
    }

    /// <summary>
    /// The store's answer is published unchanged: nothing is recounted, reordered or truncated a
    /// second time here, so the count and the names a reader sees are the ones the query produced.
    /// </summary>
    [Fact]
    public async Task The_stores_answer_is_published_unchanged()
    {
        var answer = new UnimportedProviderProductAttention(
            4, [new UnimportedProviderProductExample("901", "Monster Original")]);
        var attention = new GetUnimportedProviderProductAttention(
            new RecordingReader(answer), new FakeClock(NowUtc), new FakeBusinessCalendar(NowUtc));

        Assert.Same(answer, await attention.Handle(CancellationToken.None));
    }

    /// <summary>
    /// One reading of the clock decides both ends of the window, so the first date and the "up to
    /// now" end can never be answers about two different instants.
    /// </summary>
    [Fact]
    public async Task The_clock_is_read_once()
    {
        var clock = new CountingClock(NowUtc);
        var attention = new GetUnimportedProviderProductAttention(
            new RecordingReader(), clock, new FakeBusinessCalendar(NowUtc));

        await attention.Handle(CancellationToken.None);

        Assert.Equal(1, clock.Reads);
    }

    private sealed class RecordingReader : IUnimportedProviderProductReader
    {
        private readonly UnimportedProviderProductAttention _answer;

        public RecordingReader(UnimportedProviderProductAttention? answer = null) =>
            _answer = answer ?? new UnimportedProviderProductAttention(0, []);

        public DateTime FromUtc { get; private set; }

        public DateTime ToUtc { get; private set; }

        public int MaxExamples { get; private set; }

        public Task<UnimportedProviderProductAttention> ReadAsync(
            DateTime fromUtc, DateTime toUtc, int maxExamples, CancellationToken cancellationToken)
        {
            FromUtc = fromUtc;
            ToUtc = toUtc;
            MaxExamples = maxExamples;
            return Task.FromResult(_answer);
        }
    }

    /// <summary>
    /// A calendar whose day starts at a fixed offset from UTC midnight, standing in for a business
    /// zone that is not UTC. It is deliberately not daylight-saving aware: that is the real zoned
    /// calendar's job and is asserted against it.
    /// </summary>
    private sealed class OffsetBusinessCalendar : Inventory.Application.Time.IBusinessCalendar
    {
        private readonly TimeSpan _offsetFromUtc;

        public OffsetBusinessCalendar(DateTime nowUtc, TimeSpan offsetFromUtc)
        {
            _offsetFromUtc = offsetFromUtc;
            Today = ToBusinessDate(nowUtc);
        }

        public DateTime Today { get; }

        public DateTime ToBusinessDate(DateTime utcInstant) => (utcInstant - _offsetFromUtc).Date;

        public DateTime StartOfBusinessDayUtc(DateTime businessDate) =>
            DateTime.SpecifyKind(businessDate.Date + _offsetFromUtc, DateTimeKind.Utc);
    }

    private sealed class CountingClock : Inventory.Application.Time.IClock
    {
        private readonly DateTime _utcNow;

        public CountingClock(DateTime utcNow) => _utcNow = utcNow;

        public int Reads { get; private set; }

        public DateTime UtcNow
        {
            get
            {
                Reads++;
                return _utcNow;
            }
        }
    }
}
