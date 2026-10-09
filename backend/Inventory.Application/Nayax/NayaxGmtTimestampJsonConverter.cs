using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inventory.Application.Nayax;

/// <summary>
/// Binds a Nayax JSON field that the Nayax contract documents as GMT, applying
/// <see cref="NayaxGmtTimestamp"/>'s rule instead of .NET's default
/// <see cref="DateTimeOffset"/> binding (issue #471).
///
/// The default binding reads an offset-free value against <see cref="TimeZoneInfo.Local"/>, so it
/// answers a different instant on every host: on the Sydney-hosted API a GMT value of
/// <c>2026-10-07T23:42:44.263</c> became the instant <c>2026-10-07T12:42:44.263Z</c>, eleven hours
/// early under AEDT and ten under AEST, which moved the sale onto the previous Sydney business day.
/// This converter removes the host from the decision entirely: the field contract says GMT, so an
/// offset-free value is UTC, and a value carrying <c>Z</c> or an explicit offset keeps its physical
/// instant, normalized once.
///
/// It is attached per property - only to fields Nayax documents as GMT - rather than registered
/// globally, so no other <see cref="DateTime"/> or <see cref="DateTimeOffset"/> in the application,
/// and no date-only business value, is reinterpreted by it.
///
/// An unreadable value yields <c>null</c> rather than throwing, because the one thing worse than not
/// importing an item whose authoritative instant cannot be read is importing it at a guessed one: a
/// sale with no instant is refused by the persistence step (see <c>EfLatestNayaxSalesStore</c>) and
/// the rolling last-sales window offers the transaction again on the next refresh, while the rest of
/// the refresh still imports. Writing always emits the instant in UTC with a <c>Z</c> designator, so
/// a value this converter wrote reads back through it unchanged.
/// </summary>
public sealed class NayaxGmtTimestampJsonConverter : JsonConverter<DateTimeOffset?>
{
    /// <inheritdoc />
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return NayaxGmtTimestamp.Parse(reader.GetString());

        // Anything else does not match the documented string<date-time> contract. The value is
        // consumed (an object or array would otherwise leave the reader mid-value) and reported as
        // no instant at all.
        if (reader.TokenType != JsonTokenType.Null)
            reader.Skip();

        return null;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // A DateTime whose Kind is Utc is written as ...Z, which this converter reads back as the
        // same instant.
        writer.WriteStringValue(value.Value.UtcDateTime);
    }
}
