using Inventory.Domain.Legal;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Legal;

/// <summary>
/// One act of agreeing to the published Terms of Use and Privacy Policy, as it is recorded (issue
/// #1259).
///
/// <para><strong>What it deliberately does not carry: a name or an email address.</strong> The
/// evidence an acceptance has to supply is <em>who</em> agreed, <em>to what</em> and <em>when</em>,
/// and the Entra <c>(tid, oid)</c> pair is an identifier rather than personal detail - the same
/// reason the invite and application audit entries carry it and nothing else. A display name or a
/// contact address here would put personal data into a record nothing ever needs to read it from,
/// and into every log line that mentioned one.</para>
/// </summary>
/// <param name="BusinessId">
/// The business the agreement was given for: for an application the business the application
/// creates, for a redemption the business the invite names. Never a value a request supplied.
/// </param>
/// <param name="Identity">The agreeing person's Entra <c>(tid, oid)</c> pair, and nothing else about them.</param>
/// <param name="Version">
/// The version they agreed to, which is <see cref="LegalTerms.CurrentVersion"/>: the use case
/// refuses the request outright when the submitted version is anything else, so a stored row can
/// never describe an agreement to documents this build did not publish.
/// </param>
/// <param name="AcceptedAtUtc">
/// The UTC instant the agreement was recorded at, sampled inside the write transaction so it
/// describes the moment the application or the redemption actually happened.
/// </param>
/// <param name="Context">Which of the two acts it was.</param>
public sealed record LegalAcceptance(
    BusinessId BusinessId,
    ActorIdentity Identity,
    string Version,
    DateTime AcceptedAtUtc,
    LegalAcceptanceContext Context);

/// <summary>
/// The narrow persistence port the acceptance record is written through (issue #1259). One method,
/// one table, inserts only: there is no read, no update and no delete, because an agreement that
/// could be edited or withdrawn would not be evidence of anything.
///
/// <para><strong>Every call is made inside the application's or the redemption's own write
/// transaction</strong>, on the request's own <c>AppDbContext</c>, so the acceptance and the
/// business or membership it belongs to commit together or not at all. That is the whole reason this
/// is a row rather than only a log event: a log is delivered by a pipeline nobody can roll back,
/// and a refused application that nevertheless recorded an agreement - or an accepted one that
/// recorded none - is exactly the state this must not be able to reach.</para>
///
/// <para>Both request paths carry a <em>denied</em> <c>BusinessScope</c> - neither a person applying
/// for a business nor one joining one has a membership to resolve - and the record is nevertheless a
/// fact about one business, so it is tenant-owned and the adapter writes it through the narrow,
/// reviewed write window <c>AppDbContext.BeginLegalAcceptanceWrite</c> names
/// (docs/architecture.md § Recorded acceptance of the Terms of Use).</para>
/// </summary>
public interface ILegalAcceptanceStore
{
    /// <summary>
    /// Appends <paramref name="acceptance"/>, as one row, in the caller's open transaction.
    /// </summary>
    Task RecordAsync(LegalAcceptance acceptance, CancellationToken cancellationToken);
}
