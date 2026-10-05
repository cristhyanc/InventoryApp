using System.Text;
using Inventory.Application.Imports;
using Inventory.Infrastructure.Imports;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// Issue #380, the uploaded Nayax transaction export half of the sale-timestamp contract.
///
/// The Nayax developer portal documents the timezone semantics of the Lynx API's sales fields
/// (<c>AuthorizationDateTimeGMT</c> is the GMT instant, <c>MachineAuthorizationTime</c> is machine-local
/// wall-clock time) but publishes no contract at all for the downloadable transaction export's
/// columns. The import therefore does the only safe thing: when the export carries an
/// <c>AuthorizationDateTimeGMT</c> column it reads that as the authoritative instant, including the
/// offset-carrying ISO form a GMT column is written in; when it does not, the export's own
/// <c>MachineAuthorizationTime</c> column is read exactly as before, because inventing a conversion
/// for a contract Nayax does not publish would be the guess this issue forbids.
///
/// The offset-aware parsing is deliberately scoped to the GMT column. An offset on the machine-local
/// column would contradict what that field means, so it stays unparsable there - the behaviour
/// <see cref="ClosedXmlNayaxSalesWorkbookReaderTests"/> already pins.
/// </summary>
public class NayaxSalesExportTimestampTests
{
    /// <summary>Sunday 4 October 2026 23:30 in Sydney (AEDT, +11) is 12:30Z the same day.</summary>
    private static readonly DateTime SundayEveningUtc = new(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2026-10-04T12:30:00Z")]
    [InlineData("2026-10-04T12:30:00.000Z")]
    [InlineData("2026-10-04T12:30:00+00:00")]
    [InlineData("2026-10-04T23:30:00+11:00")]
    [InlineData("2026-10-04T22:30:00+10:00")]
    public void An_offset_carrying_GMT_column_is_read_as_the_instant_it_names(string cell)
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,AuthorizationDateTimeGMT\n" +
            $"1001,7,{cell}"));

        Assert.Equal(SundayEveningUtc, row.AuthorizationDateTimeGmt);
        Assert.Equal(SundayEveningUtc, row.AuthorizationTime);
        Assert.Equal(DateTimeKind.Utc, row.AuthorizationTime!.Value.Kind);
    }

    /// <summary>
    /// A GMT column written without any designator is still a GMT column: the header names the
    /// timezone, so the value is read as UTC rather than left unparsable.
    /// </summary>
    [Fact]
    public void A_GMT_column_without_a_designator_is_read_as_UTC()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,AuthorizationDateTimeGMT\n" +
            "1001,7,2026-10-04T12:30:00"));

        Assert.Equal(SundayEveningUtc, row.AuthorizationTime);
    }

    /// <summary>
    /// Header spelling tolerance matches the rest of the reader: letters and digits only,
    /// case-insensitively.
    /// </summary>
    [Theory]
    [InlineData("AuthorizationDateTimeGMT")]
    [InlineData("Authorization Date Time GMT")]
    [InlineData("authorization_datetime_gmt")]
    public void Every_supported_GMT_header_spelling_is_read(string header)
    {
        var row = Assert.Single(ReadCsv(
            $"TransactionID,MachineID,{header}\n" +
            "1001,7,2026-10-04T12:30:00Z"));

        Assert.Equal(SundayEveningUtc, row.AuthorizationTime);
    }

    /// <summary>
    /// An export carrying both columns is timestamped from the authoritative one. The machine-local
    /// value stays readable as the raw fact it is, and is what moved a Sunday-evening sale into Monday
    /// when it was used as the instant.
    /// </summary>
    [Fact]
    public void The_GMT_column_is_preferred_over_the_machine_local_column()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
            "1001,7,4/10/2026 11:30:00 PM,2026-10-04T12:30:00Z"));

        Assert.Equal(SundayEveningUtc, row.AuthorizationTime);
        Assert.Equal(new DateTime(2026, 10, 4, 23, 30, 0), row.MachineAuthorizationTime);
    }

    /// <summary>
    /// No GMT column, or one the export left empty or unparsable: the row keeps the export's own
    /// machine-local value exactly as previous imports read it, because the export's timezone contract
    /// is undocumented and a conversion would be a guess. Such an import's instants are unverified -
    /// see docs/architecture.md § Nayax sale timestamps.
    /// </summary>
    [Theory]
    [InlineData("TransactionID,MachineID,MachineAuthorizationTime\n1001,7,4/10/2026 11:30:00 PM")]
    [InlineData("TransactionID,MachineID,AuthorizationDateTimeGMT,MachineAuthorizationTime\n1001,7,,4/10/2026 11:30:00 PM")]
    [InlineData("TransactionID,MachineID,AuthorizationDateTimeGMT,MachineAuthorizationTime\n1001,7,not-an-instant,4/10/2026 11:30:00 PM")]
    public void Without_an_authoritative_GMT_column_the_export_s_own_value_is_read_unchanged(string content)
    {
        var row = Assert.Single(ReadCsv(content));

        Assert.Null(row.AuthorizationDateTimeGmt);
        Assert.Equal(new DateTime(2026, 10, 4, 23, 30, 0), row.AuthorizationTime);
    }

    /// <summary>
    /// A row carrying neither timestamp has no instant at all, which is what makes
    /// <c>ImportNayaxSales</c> skip it instead of importing it at a guessed time.
    /// </summary>
    [Fact]
    public void A_row_with_neither_timestamp_has_no_authorization_time()
    {
        var row = Assert.Single(ReadCsv(
            "TransactionID,MachineID,AuthorizationDateTimeGMT,MachineAuthorizationTime\n" +
            "1001,7,,"));

        Assert.Null(row.AuthorizationTime);
    }

    private static IReadOnlyList<NayaxSalesImportRow> ReadCsv(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new ClosedXmlNayaxSalesWorkbookReader().Read(stream, "sales.csv");
    }
}
