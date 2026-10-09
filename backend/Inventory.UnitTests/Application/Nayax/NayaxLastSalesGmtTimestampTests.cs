using System.Globalization;
using System.Text.Json;
using Inventory.Application.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// Issue #471: how <see cref="NayaxLastSalesReport.AuthorizationDateTimeGmt"/> binds the live
/// payload's authorization timestamp, which is a follow-up to the field-selection fix of issue #380.
///
/// The authoritative contract is Nayax's own published one for
/// <c>GET /v1/machines/{MachineID}/lastSales</c>
/// ("Get Last Sales for Machine by MachineID",
/// https://devzone.nayax.com/reference/lynx/machines/get-last-sales-for-machine-by-machineid), read
/// through the Nayax documentation MCP server. It declares
/// <c>AuthorizationDateTimeGMT: string&lt;date-time&gt;</c>, "The date and time when the transaction
/// was authorized, in GMT", and the portal's own live sample response
/// (<c>docs/manage-data-operations/lynx-api/machines/getting-a-machines-last-sales-ereceipt-information</c>)
/// prints that field <em>without</em> a designator - <c>"AuthorizationDateTimeGMT":
/// "2026-02-08T09:31:51.817"</c> - two hours behind the same item's machine-local
/// <c>MachineAuthorizationTime</c>. So the field's UTC meaning comes from the field contract, never
/// from a trailing <c>Z</c> and never from the host the process happens to run on.
///
/// That is the defect: .NET's own JSON binding of an offset-free value applies
/// <see cref="TimeZoneInfo.Local"/>'s offset
/// (<see cref="The_frameworks_own_binding_of_an_offset_free_value_follows_the_host_offset"/>), so on
/// the Sydney-hosted API a GMT value of <c>2026-10-07T23:42:44.263</c> became the instant
/// <c>2026-10-07T12:42:44.263Z</c> - eleven hours early under AEDT, ten under AEST - which is exactly
/// the stored value the operator reported on 8 October 2026. The sale then falls on the previous
/// Sydney business day, moving revenue between days and weeks.
///
/// These tests deliberately never mutate the process time zone: <see cref="TimeZoneInfo.Local"/> is
/// process-global state that the parallel test collections would race on. A host offset is simulated
/// deterministically instead (<see cref="AsAHostWouldBind"/>), and the one assertion that must speak
/// about the real host reads the real host's offset.
/// </summary>
public class NayaxLastSalesGmtTimestampTests
{
    /// <summary>
    /// The sanitized live value the operator reported (issue #471), offset-free as the portal's own
    /// live sample is. Sydney was on AEDT (+11), so a host-local reading is eleven hours out.
    /// </summary>
    private const string OperatorGmt = "2026-10-07T23:42:44.263";

    /// <summary>The instant that value names: 2026-10-07T23:42:44.263Z, Sydney 8 October 10:42:44.263.</summary>
    private static readonly DateTime OperatorInstantUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>The same shape on an ordinary AEST day, where a host-local reading is ten hours out.</summary>
    private const string WinterGmt = "2026-07-14T23:42:44.263";

    private static readonly DateTime WinterInstantUtc = new(2026, 7, 14, 23, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>
    /// The options the production read path uses: <c>HttpClient.ReadFromJsonAsync</c> (and so
    /// <c>NayaxLynxClient.GetMachineLastSalesAsync</c>) deserializes with
    /// <see cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The documented response item, with the machine-local field deliberately different from the GMT
    /// field - the only shape in which the two can be told apart - and offset-free exactly as both
    /// arrive from the live endpoint.
    /// </summary>
    private static string LastSalesPayload(string authorizationDateTimeGmt) => $$"""
        [
          {
            "TransactionID": 3564567268,
            "MachineID": 531595328,
            "MachineName": "Pavillion Right",
            "SettlementValue": 3.0000,
            "AuthorizationDateTimeGMT": "{{authorizationDateTimeGmt}}",
            "MachineAuthorizationTime": "2026-10-08T10:42:44.093",
            "SettlementDateTimeGMT": "{{authorizationDateTimeGmt}}"
          }
        ]
        """;

    private static NayaxLastSalesReport Deserialize(string payload) =>
        Assert.Single(JsonSerializer.Deserialize<List<NayaxLastSalesReport>>(payload, Web)!);

    /// <summary>
    /// The instant a host whose offset is <paramref name="hostOffset"/> would have produced for an
    /// offset-free value, which is what the defect did. Simulated rather than installed, so no test
    /// touches process-global time zone state.
    /// </summary>
    private static DateTime AsAHostWouldBind(string offsetFree, TimeSpan hostOffset) =>
        new DateTimeOffset(
            DateTime.Parse(offsetFree, CultureInfo.InvariantCulture, DateTimeStyles.None),
            hostOffset).UtcDateTime;

    /// <summary>
    /// The headline case: the operator's exact reported value binds to the instant its field contract
    /// names, with no host offset applied.
    /// </summary>
    [Fact]
    public void The_operator_s_offset_free_GMT_value_is_the_UTC_instant_the_field_contract_names()
    {
        var sale = Deserialize(LastSalesPayload(OperatorGmt));

        Assert.Equal(OperatorInstantUtc, sale.AuthorizationInstantUtc);
        Assert.Equal(DateTimeKind.Utc, sale.AuthorizationInstantUtc!.Value.Kind);
        Assert.Equal(TimeSpan.Zero, sale.AuthorizationDateTimeGmt!.Value.Offset);
    }

    /// <summary>
    /// The same contract on an ordinary AEST day, so the fix is not an eleven-hour compensation that
    /// happens to suit daylight saving.
    /// </summary>
    [Fact]
    public void An_offset_free_GMT_value_outside_daylight_saving_is_read_the_same_way()
    {
        var sale = Deserialize(LastSalesPayload(WinterGmt));

        Assert.Equal(WinterInstantUtc, sale.AuthorizationInstantUtc);
    }

    /// <summary>
    /// The instant must be the field contract's, whatever offset a host would have applied: a UTC
    /// host, a Sydney host on AEST (+10) and on AEDT (+11), a host east of Sydney and one west of UTC.
    /// Only a UTC host ever agreed with the contract, which is why the defect was invisible in CI and
    /// visible in production.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(-5)]
    public void The_bound_instant_is_independent_of_the_offset_any_host_would_apply(int hostOffsetHours)
    {
        var hostOffset = TimeSpan.FromHours(hostOffsetHours);

        var sale = Deserialize(LastSalesPayload(OperatorGmt));

        Assert.Equal(OperatorInstantUtc, sale.AuthorizationInstantUtc);
        Assert.Equal(
            hostOffsetHours == 0,
            AsAHostWouldBind(OperatorGmt, hostOffset) == OperatorInstantUtc);
    }

    /// <summary>
    /// Why the field declares its own converter at all: .NET's default binding of an offset-free
    /// value to <see cref="DateTimeOffset"/> adopts <see cref="TimeZoneInfo.Local"/>'s offset, so it
    /// answers a different instant on every host. This is the mechanism of the reported defect, read
    /// off the host this test actually runs on - under <c>TZ=Australia/Sydney</c> it asserts the
    /// eleven-hour shift the operator saw, under a UTC host it asserts the absence of one, and it
    /// fails if the framework's own rule ever changes.
    /// </summary>
    [Fact]
    public void The_frameworks_own_binding_of_an_offset_free_value_follows_the_host_offset()
    {
        var wallClock = DateTime.Parse(OperatorGmt, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var hostOffset = TimeZoneInfo.Local.GetUtcOffset(wallClock);

        var defaultBinding = JsonSerializer.Deserialize<DateTimeOffset>($"\"{OperatorGmt}\"", Web);

        Assert.Equal(hostOffset, defaultBinding.Offset);
        Assert.Equal(AsAHostWouldBind(OperatorGmt, hostOffset), defaultBinding.UtcDateTime);
        Assert.Equal(OperatorInstantUtc - hostOffset, defaultBinding.UtcDateTime);
    }

    /// <summary>
    /// A value that does carry a designator or an explicit offset keeps its physical instant and is
    /// normalized exactly once: <c>Z</c>, <c>+00:00</c>, the Sydney AEDT and AEST renderings of the
    /// same instant, and an offset west of UTC all name 2026-10-07T23:42:44.263Z.
    /// </summary>
    [Theory]
    [InlineData("2026-10-07T23:42:44.263Z")]
    [InlineData("2026-10-07T23:42:44.263+00:00")]
    [InlineData("2026-10-08T10:42:44.263+11:00")]
    [InlineData("2026-10-08T09:42:44.263+10:00")]
    [InlineData("2026-10-07T18:42:44.263-05:00")]
    public void An_explicit_designator_or_offset_preserves_the_physical_instant(string authorizationDateTimeGmt)
    {
        var sale = Deserialize(LastSalesPayload(authorizationDateTimeGmt));

        Assert.Equal(OperatorInstantUtc, sale.AuthorizationInstantUtc);
        Assert.Equal(TimeSpan.Zero, sale.AuthorizationDateTimeGmt!.Value.Offset);
    }

    /// <summary>
    /// Deserializing repeatedly, and re-serializing and reading the item back, cannot shift the
    /// instant: the offset-free form, the <c>Z</c> form and the written form all answer the same
    /// instant. That is what makes the rolling last-sales window, which returns a transaction again on
    /// every refresh, unable to move a sale a second time.
    /// </summary>
    [Fact]
    public void Repeated_deserialization_and_a_round_trip_do_not_shift_the_instant()
    {
        var first = Deserialize(LastSalesPayload(OperatorGmt));
        var second = Deserialize(LastSalesPayload(OperatorGmt));
        Assert.Equal(first.AuthorizationInstantUtc, second.AuthorizationInstantUtc);

        var written = JsonSerializer.Serialize(new List<NayaxLastSalesReport> { first }, Web);
        var roundTripped = Assert.Single(
            JsonSerializer.Deserialize<List<NayaxLastSalesReport>>(written, Web)!);
        Assert.Equal(OperatorInstantUtc, roundTripped.AuthorizationInstantUtc);

        var again = Assert.Single(JsonSerializer.Deserialize<List<NayaxLastSalesReport>>(
            JsonSerializer.Serialize(new List<NayaxLastSalesReport> { roundTripped }, Web), Web)!);
        Assert.Equal(OperatorInstantUtc, again.AuthorizationInstantUtc);
    }

    /// <summary>
    /// Fail closed: an item whose authoritative timestamp is absent, null, empty or unreadable has no
    /// instant at all. It must never become <see cref="DateTimeOffset.MinValue"/>, today's date, or
    /// the machine-local wall clock the same item carries - the persistence step skips a sale with no
    /// instant, and the rolling window returns it again on the next refresh.
    /// </summary>
    [Theory]
    // No such property on the item.
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    // Explicitly null.
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": null }]""")]
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": "" }]""")]
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": "   " }]""")]
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": "not-an-instant" }]""")]
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": "2026-13-45T99:99:99" }]""")]
    // A structurally wrong value: the payload did not match its contract at all.
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": 1760000000 }]""")]
    [InlineData("""[{ "TransactionID": 1, "MachineID": 2, "AuthorizationDateTimeGMT": { "value": "2026-10-07T23:42:44.263" } }]""")]
    public void A_missing_or_unreadable_authoritative_timestamp_has_no_instant(string payload)
    {
        var sale = Deserialize(payload);

        Assert.Null(sale.AuthorizationDateTimeGmt);
        Assert.Null(sale.AuthorizationInstantUtc);
    }

    /// <summary>
    /// Fail closed at the JSON boundary on a value that is malformed but parseable. The field is
    /// declared <c>string&lt;date-time&gt;</c>, so a date-only value, an ambiguous slash date, or any
    /// other non-contract rendering has no instant: it must not become midnight UTC, the wrong
    /// calendar month, or - the point of this test, since every payload here carries one - the
    /// machine-local <c>MachineAuthorizationTime</c> beside it.
    /// </summary>
    [Theory]
    // Date only: 2026-10-07T00:00:00Z would be an invented midnight, 11:00 on 7 October in Sydney.
    [InlineData("2026-10-07")]
    // Ambiguous: 7 October to this operator, 10 July to an invariant-culture reader.
    [InlineData("07/10/2026")]
    [InlineData("07/10/2026 23:42:44")]
    // Formats the GMT field's contract does not use.
    [InlineData("Wed, 07 Oct 2026 23:42:44 GMT")]
    [InlineData("October 7, 2026 11:42:44 PM")]
    [InlineData("20261007T234244Z")]
    public void A_malformed_but_parseable_authoritative_timestamp_has_no_instant(string authorizationDateTimeGmt)
    {
        var sale = Deserialize(LastSalesPayload(authorizationDateTimeGmt));

        Assert.Null(sale.AuthorizationDateTimeGmt);
        Assert.Null(sale.AuthorizationInstantUtc);
        Assert.Equal(new DateTime(2026, 10, 8, 10, 42, 44, 93), sale.MachineAuthorizationTime);
    }

    /// <summary>
    /// The machine-local field is untouched by this change: it stays the raw wall-clock fact the
    /// Nayax contract says it is, is not reinterpreted as an instant, and is never substituted for a
    /// missing GMT value.
    /// </summary>
    [Fact]
    public void The_machine_local_field_is_still_a_raw_wall_clock_fact()
    {
        var sale = Deserialize(LastSalesPayload(OperatorGmt));

        Assert.Equal(new DateTime(2026, 10, 8, 10, 42, 44, 93), sale.MachineAuthorizationTime);
        Assert.NotEqual(sale.MachineAuthorizationTime, sale.AuthorizationInstantUtc);
    }
}
