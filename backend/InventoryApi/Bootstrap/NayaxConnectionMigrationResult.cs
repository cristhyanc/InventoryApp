using Inventory.Domain.Nayax;

namespace InventoryApi.Bootstrap;

/// <summary>Why the Nayax connection migration refused to run, or that it succeeded.</summary>
public enum NayaxConnectionMigrationOutcome
{
    /// <summary>The run completed, or - in a dry run - resolved a plan without writing.</summary>
    Succeeded = 0,

    /// <summary>
    /// The global <c>NayaxLynx</c> configuration holds no operator id, no access token, or
    /// neither. There is nothing to move, and this command never invents a credential.
    /// </summary>
    ConfigurationInvalid = 1,

    /// <summary>
    /// The database has pending migrations, so the connection table may not exist yet or may not
    /// be at the shape this code expects. Apply the schema first and review it.
    /// </summary>
    SchemaNotReady = 2,

    /// <summary>
    /// Tenant ownership is not bootstrapped: there is not exactly one active business with a
    /// usable membership and no unassigned rows. More than one business existing lands here, and
    /// which business owns the credential is then a human decision rather than a guess.
    /// </summary>
    TenantOwnershipNotBootstrapped = 3,

    /// <summary>
    /// The single business is not the one <c>bootstrap-business</c> adopted: no
    /// <c>BusinessBackfillAudit</c> row names it. Writing a production credential onto a business
    /// whose provenance cannot be shown is refused.
    /// </summary>
    BusinessNotBootstrapped = 4,

    /// <summary>
    /// The business already holds a <em>different</em> operator id or access token. Replacing a
    /// credential somebody else stored is a human decision, not something a migration does on its
    /// own, so nothing is written.
    /// </summary>
    DifferentCredentialsStored = 5,

    /// <summary>
    /// No usable Nayax token encryption key is configured, or the stored ciphertext cannot be
    /// decrypted with the configured keys. The token is never stored unencrypted instead; the
    /// message names the setting to fix and carries no token, ciphertext or key material.
    /// </summary>
    TokenProtectionUnavailable = 6,

    /// <summary>
    /// The credentials were stored, but the <see cref="NayaxConnectionStatus.Ready"/> status was
    /// not applied because the stored credential revision changed in between - something else
    /// saved a credential while this command ran. Re-run the command.
    /// </summary>
    StatusNotApplied = 7,
}

/// <summary>What the migration did, or - in a dry run - what an apply would do.</summary>
public enum NayaxConnectionMigrationChange
{
    /// <summary>
    /// Nothing. The business already holds exactly this operator id and token, already marked
    /// <see cref="NayaxConnectionStatus.Ready"/>, which is what makes a re-run idempotent.
    /// </summary>
    None = 0,

    /// <summary>
    /// The configured operator id and token are stored for the business, encrypted, and the
    /// connection is marked <see cref="NayaxConnectionStatus.Ready"/>.
    /// </summary>
    CredentialsStored = 1,

    /// <summary>
    /// The same operator id and token are already stored, but not yet marked
    /// <see cref="NayaxConnectionStatus.Ready"/>, so only the status is written. The credential is
    /// not re-saved, so the revision does not move and the stored ciphertext is left alone.
    /// </summary>
    StatusMarkedReady = 2,
}

/// <summary>
/// The complete, reviewable outcome of one <c>migrate-nayax-connection</c> invocation (issue
/// #519).
///
/// Everything here is safe to print and safe to log. The operator id is a remote identity and not
/// a secret; the access token appears in no member, no message and no printed line, which is the
/// property the command's tests assert directly rather than trusting review to notice.
/// </summary>
public sealed record NayaxConnectionMigrationResult
{
    public required NayaxConnectionMigrationOutcome Outcome { get; init; }

    /// <summary>True when this was a dry run, which writes nothing at all.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Operator-facing explanation. Never contains an access token.</summary>
    public required string Message { get; init; }

    /// <summary>The business the credential was (or would be) stored for, once resolved.</summary>
    public int? BusinessId { get; init; }

    /// <summary>What happened, or what an apply would do. <see cref="NayaxConnectionMigrationChange.None"/> on a refusal.</summary>
    public NayaxConnectionMigrationChange Change { get; init; }

    /// <summary>The operator id read from configuration, once it was found to be present.</summary>
    public string? ConfiguredOperatorId { get; init; }

    /// <summary>The operator id already stored for the business, or <see langword="null"/> when no record exists.</summary>
    public string? StoredOperatorId { get; init; }

    /// <summary>
    /// Whether the stored access token is byte-for-byte the configured one. Reported as a plain
    /// yes/no - never as a prefix, a length or a hash - because anything derived from a token is
    /// still something a console transcript should not carry.
    /// </summary>
    public bool? StoredTokenMatchesConfigured { get; init; }

    /// <summary>The connection status before this run, or <see langword="null"/> when there was no record.</summary>
    public NayaxConnectionStatus? StatusBefore { get; init; }

    /// <summary>
    /// The connection status after this run. On a dry run it is the status an apply would leave,
    /// which is reported as a prediction rather than measured, because a dry run writes nothing.
    /// </summary>
    public NayaxConnectionStatus? StatusAfter { get; init; }

    /// <summary>The credential revision before this run, or <see langword="null"/> when there was no record.</summary>
    public int? CredentialRevisionBefore { get; init; }

    /// <summary>The credential revision after this run, predicted on a dry run.</summary>
    public int? CredentialRevisionAfter { get; init; }

    public bool Succeeded => Outcome == NayaxConnectionMigrationOutcome.Succeeded;
}
