using System.Text.Json.Serialization;
using Inventory.Domain.Legal;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One recorded acceptance of the published Terms of Use and Privacy Policy (issue #1259):
/// append-only evidence that this person agreed to this version of those documents at this instant,
/// while doing one of the two things the Terms themselves name - applying for a business or joining
/// one.
///
/// <para><strong>It is written in the same transaction as the application or the redemption it
/// belongs to</strong>, so there is no moment at which a database holds a business somebody started
/// with no record of the agreement they gave, or a record of an agreement whose application was
/// rolled back. That is the whole reason it is a row here rather than only a log event, exactly as
/// it is for <see cref="BusinessPlanGrantAudit"/>.</para>
///
/// <para><strong>Tenant-owned, with nothing added.</strong> An acceptance is a fact about one
/// business, so <c>AppDbContext</c> gives it the business key, the index, the query filter and the
/// ownership stamp. Both paths that write one carry a <em>denied</em> business scope, so the insert
/// goes through the narrow, reviewed write window of
/// <c>AppDbContext.BeginLegalAcceptanceWrite</c> - one entity type, inserts only, one business whose
/// id the operation's own write transaction produced. Nothing updates or deletes a row: there is no
/// edit path and no withdrawal path, because an agreement that could be rewritten is not
/// evidence.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> No display name and no email address
/// - not the person's, not the business's. The agreeing person is the Entra <c>(tid, oid)</c> pair,
/// which is an identifier rather than personal detail and is what an investigation needs; a name or
/// an address here would be personal data in a record nothing reads it from. There is no "accepted"
/// flag either: the row existing <em>is</em> the acceptance.</para>
/// </summary>
public class BusinessLegalAcceptance : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The agreeing person's Entra <c>tid</c>.</summary>
    public required string DirectoryTenantId { get; set; }

    /// <summary>Their Entra <c>oid</c>.</summary>
    public required string ObjectId { get; set; }

    /// <summary>
    /// The version of the documents they agreed to, as
    /// <c>Inventory.Domain.Legal.LegalTerms.CurrentVersion</c> publishes it. Stored as the text it
    /// is rather than as a number or a date column: it is the identifier of a published document,
    /// and a date type would invite a time zone conversion of something that has none.
    /// </summary>
    public required string Version { get; set; }

    /// <summary>When the agreement was recorded, from the application clock, inside the transaction.</summary>
    public DateTime AcceptedAtUtc { get; set; }

    /// <summary>
    /// Which act it was. Required and stated rather than inferred from anything else on the row: an
    /// application and a redemption both produce one acceptance for one business, so nothing else
    /// here could tell them apart.
    /// </summary>
    public required LegalAcceptanceContext Context { get; set; }
}
