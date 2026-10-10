using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Nayax;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Moves the globally configured Nayax operator id and access token into the existing business's
/// own encrypted connection record (issue #519, a slice of #500).
///
/// It is the data half of <see cref="NayaxConnectionMigrationCommand"/>, separated from it so the
/// refusals and the idempotency can be tested against a real relational database without a web
/// host, a configuration provider or a console.
///
/// The four properties that make it safe to point at a production credential:
/// <list type="bullet">
///   <item><b>A dry run writes nothing at all.</b> Unlike <c>bootstrap-business</c>, which measures
///   by doing the work and rolling back, this plan is two values and a status: it can be reported
///   exactly without writing, so a dry run does not open a transaction on the credential table at
///   all.</item>
///   <item><b>Idempotent.</b> A business already holding exactly this operator id and token, marked
///   <see cref="NayaxConnectionStatus.Ready"/>, is left completely untouched - the row is not
///   rewritten, so not even the ciphertext changes and the credential revision does not move.</item>
///   <item><b>It never replaces a credential it did not recognise.</b> A stored operator id or
///   token that differs from the configured one, or a ciphertext the configured keys cannot
///   decrypt, is a refusal. Whether that row or the configuration is the credential in production
///   use is precisely what this command cannot know.</item>
///   <item><b>The token never leaves the ciphertext column.</b> It is not in a result member, a
///   message, an exception or a printed line. Encryption, and every decision about keys, belongs to
///   the store this calls (issue #518) - this type hands it a plaintext token once and keeps none
///   of it.</item>
/// </list>
///
/// It defines no Nayax request or response contract. The operator id it moves is the Lynx API's
/// <c>OperatorID</c> path parameter and the token is its <c>Authorization: Bearer</c> credential,
/// both confirmed against the Nayax developer documentation, and both already modelled by #518.
/// Nothing here calls Nayax: the status it writes records that the credential is the one already in
/// production use, not the result of a live permission test.
/// </summary>
public sealed class NayaxConnectionMigrator
{
    private readonly AppDbContext _tenancyDb;
    private readonly Func<int, INayaxConnectionStore> _storeFactory;
    private readonly string? _configuredOperatorId;
    private readonly string? _configuredAccessToken;
    private readonly IClock _clock;

    /// <param name="tenancyDb">
    /// A context used only to resolve and verify the target business. It needs no tenant scope and
    /// deliberately is not given an unrestricted one: <c>Business</c>, <c>BusinessMembership</c>
    /// and <c>BusinessBackfillAudit</c> carry no tenant query filter, so a denied context reads
    /// them while reaching no tenant-owned row at all.
    /// </param>
    /// <param name="storeFactory">
    /// Produces the issue-#518 connection store over a context scoped to the business id it is
    /// given, which is how every read and write of the credential stays inside the central
    /// ownership boundary.
    /// </param>
    /// <param name="configuredOperatorId">The global <c>NayaxLynx:OperatorId</c> value.</param>
    /// <param name="configuredAccessToken">
    /// The global access token, already resolved from its two possible configuration keys by
    /// <see cref="NayaxLynxConfiguration.ResolveAccessToken"/>. A secret: it reaches the store and
    /// nothing else.
    /// </param>
    /// <param name="clock">Supplies the instant recorded with the status, so tests can pin it.</param>
    public NayaxConnectionMigrator(
        AppDbContext tenancyDb,
        Func<int, INayaxConnectionStore> storeFactory,
        string? configuredOperatorId,
        string? configuredAccessToken,
        IClock clock)
    {
        _tenancyDb = tenancyDb;
        _storeFactory = storeFactory;
        _configuredOperatorId = configuredOperatorId;
        _configuredAccessToken = configuredAccessToken;
        _clock = clock;
    }

    public async Task<NayaxConnectionMigrationResult> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuredOperatorId) || string.IsNullOrWhiteSpace(_configuredAccessToken))
        {
            return Refusal(
                NayaxConnectionMigrationOutcome.ConfigurationInvalid,
                dryRun,
                $"{NayaxLynxOptions.SectionName}:{nameof(NayaxLynxOptions.OperatorId)} and the "
                    + $"configured access token ({NayaxLynxOptions.SectionName}:"
                    + $"{nameof(NayaxLynxOptions.AccessToken)}, or the legacy Nayax:Token key) are "
                    + "both required: this command moves the credential the application already "
                    + "uses and never invents one. Nothing was written.");
        }

        var operatorId = _configuredOperatorId.Trim();
        var accessToken = _configuredAccessToken;

        var pending = await _tenancyDb.Database.GetPendingMigrationsAsync(cancellationToken);
        var pendingCount = pending.Count();
        if (pendingCount > 0)
        {
            return Refusal(
                NayaxConnectionMigrationOutcome.SchemaNotReady,
                dryRun,
                $"The database has {pendingCount.ToString(CultureInfo.InvariantCulture)} pending "
                    + "migration(s), so the per-business Nayax connection table may not exist or may "
                    + "not be at the expected shape. Apply the schema first, verify it, then run "
                    + "this command as a separate, reviewed step.");
        }

        // The repository's own definition of "this database has been bootstrapped" (issue #64):
        // no unassigned rows, exactly one business which is active, and at least one usable
        // membership. More than one business lands here too, which is the refusal the issue asks
        // for - which business owns the credential is then a human decision.
        var readiness = TenantOwnershipReadiness.Inspect(_tenancyDb);
        if (!readiness.IsReady)
        {
            return Refusal(
                NayaxConnectionMigrationOutcome.TenantOwnershipNotBootstrapped,
                dryRun,
                $"Tenant ownership is not bootstrapped: {Count(readiness.UnassignedRows)} unassigned "
                    + $"row(s), {Count(readiness.BusinessCount)} business(es) of which "
                    + $"{Count(readiness.ActiveBusinessCount)} active, and "
                    + $"{Count(readiness.UsableMembershipCount)} usable membership(s). This command "
                    + "stores the credential for exactly one bootstrapped, active business with "
                    + "someone who can reach it; with more than one business, which of them owns the "
                    + "credential is a human decision. See docs/tenant-rollout.md. Nothing was written.");
        }

        var businessId = await _tenancyDb.Businesses
            .OrderBy(business => business.Id)
            .Select(business => business.Id)
            .SingleAsync(cancellationToken);

        // The durable evidence that `bootstrap-business` adopted *this* business, written in the
        // same transaction as the backfill. Without it the single business is not demonstrably the
        // bootstrapped one, and a production credential is not written onto a business whose
        // provenance cannot be shown.
        var bootstrapped = await _tenancyDb.BusinessBackfillAudits
            .AnyAsync(audit => audit.BusinessId == businessId, cancellationToken);

        if (!bootstrapped)
        {
            return Refusal(
                NayaxConnectionMigrationOutcome.BusinessNotBootstrapped,
                dryRun,
                "No BusinessBackfillAudit row names the single business, so it is not the business "
                    + "`bootstrap-business` adopted. This command only stores the existing "
                    + "credential onto the bootstrapped business; a business that arrived some other "
                    + "way is a human decision. See docs/tenant-rollout.md. Nothing was written.",
                businessId,
                operatorId);
        }

        var store = _storeFactory(businessId);

        try
        {
            return await MigrateAsync(store, businessId, operatorId, accessToken, dryRun, cancellationToken);
        }
        catch (NayaxTokenProtectionException protectionFailure)
        {
            // Fail closed: a token that cannot be encrypted is never stored in some other form, and
            // a stored ciphertext that cannot be decrypted is never treated as absent or as a
            // different credential and overwritten. The message names the setting at fault and
            // carries no token, ciphertext or key material.
            return Refusal(
                NayaxConnectionMigrationOutcome.TokenProtectionUnavailable,
                dryRun,
                $"{protectionFailure.Message} Nothing was written.",
                businessId,
                operatorId);
        }
    }

    private async Task<NayaxConnectionMigrationResult> MigrateAsync(
        INayaxConnectionStore store,
        int businessId,
        string operatorId,
        string accessToken,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var existing = await store.FindAsync(cancellationToken);

        bool? storedTokenMatches = null;
        NayaxConnectionMigrationChange plan;

        if (existing is null)
        {
            plan = NayaxConnectionMigrationChange.CredentialsStored;
        }
        else
        {
            // Throws when the stored ciphertext cannot be decrypted, which RunAsync turns into a
            // refusal rather than a comparison against an empty token.
            var stored = await store.FindCredentialAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Nayax connection record that was just read for this business reports no "
                        + "credential, so the migration refuses rather than acting on a state the "
                        + "database may no longer hold.");

            storedTokenMatches = TokensMatch(stored.AccessToken, accessToken);
            var operatorMatches = string.Equals(stored.OperatorId, operatorId, StringComparison.Ordinal);

            if (!operatorMatches || storedTokenMatches != true)
            {
                return new NayaxConnectionMigrationResult
                {
                    Outcome = NayaxConnectionMigrationOutcome.DifferentCredentialsStored,
                    DryRun = dryRun,
                    BusinessId = businessId,
                    ConfiguredOperatorId = operatorId,
                    StoredOperatorId = stored.OperatorId,
                    StoredTokenMatchesConfigured = storedTokenMatches,
                    StatusBefore = existing.Status,
                    StatusAfter = existing.Status,
                    CredentialRevisionBefore = existing.CredentialRevision,
                    CredentialRevisionAfter = existing.CredentialRevision,
                    Message =
                        $"This business already holds a different Nayax credential (stored operator "
                            + $"id '{stored.OperatorId}', configured '{operatorId}'; the stored access "
                            + $"token {(storedTokenMatches == true ? "matches" : "does not match")} the "
                            + "configured one). Replacing a credential something else stored is a "
                            + "human decision, not something a migration makes on its own, so nothing "
                            + "was written. Confirm which credential is correct first.",
                };
            }

            plan = existing.Status == NayaxConnectionStatus.Ready
                ? NayaxConnectionMigrationChange.None
                : NayaxConnectionMigrationChange.StatusMarkedReady;
        }

        if (dryRun)
        {
            return new NayaxConnectionMigrationResult
            {
                Outcome = NayaxConnectionMigrationOutcome.Succeeded,
                DryRun = true,
                Change = plan,
                BusinessId = businessId,
                ConfiguredOperatorId = operatorId,
                StoredOperatorId = existing?.OperatorId,
                StoredTokenMatchesConfigured = storedTokenMatches,
                StatusBefore = existing?.Status,
                // Predicted, not measured: a dry run writes nothing, so it cannot read the result
                // back. The plan is two values and a status, which is why predicting it is exact.
                StatusAfter = NayaxConnectionStatus.Ready,
                CredentialRevisionBefore = existing?.CredentialRevision,
                CredentialRevisionAfter = existing?.CredentialRevision ?? 1,
                Message = DryRunMessage(plan),
            };
        }

        var notApplied = await ApplyPlanAsync(store, plan, existing, businessId, operatorId, accessToken, cancellationToken);
        if (notApplied is not null)
        {
            return notApplied;
        }

        // Measured, not intended: the revision is computed in the database, so only the row can
        // say what the business now holds - including on the idempotent no-op path.
        var after = await store.FindAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The business's Nayax connection cannot be read back after the migration, so the "
                    + "run reports a failure rather than values the database may not hold.");

        return new NayaxConnectionMigrationResult
        {
            Outcome = NayaxConnectionMigrationOutcome.Succeeded,
            DryRun = false,
            Change = plan,
            BusinessId = businessId,
            ConfiguredOperatorId = operatorId,
            StoredOperatorId = after.OperatorId,
            StoredTokenMatchesConfigured = true,
            StatusBefore = existing?.Status,
            StatusAfter = after.Status,
            CredentialRevisionBefore = existing?.CredentialRevision,
            CredentialRevisionAfter = after.CredentialRevision,
            Message = AppliedMessage(plan),
        };
    }

    // Ready rather than the PendingPermissions a save leaves: the token being stored is the one
    // the application is authenticating to Nayax with today, so its permissions are evidenced by
    // production use - this command never calls Nayax. Returns the StatusNotApplied result if the
    // conditional status update was discarded, or null when the plan's write succeeded (or there
    // was nothing to write).
    private async Task<NayaxConnectionMigrationResult?> ApplyPlanAsync(
        INayaxConnectionStore store,
        NayaxConnectionMigrationChange plan,
        NayaxConnection? existing,
        int businessId,
        string operatorId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (plan == NayaxConnectionMigrationChange.CredentialsStored)
        {
            var saved = await store.SaveCredentialAsync(operatorId, accessToken, cancellationToken);

            return await ApplyReadyAsync(store, saved.CredentialRevision, cancellationToken)
                ? null
                : StatusNotApplied(businessId, operatorId, plan, existing, saved.CredentialRevision);
        }

        if (plan == NayaxConnectionMigrationChange.StatusMarkedReady
            && !await ApplyReadyAsync(store, existing!.CredentialRevision, cancellationToken))
        {
            return StatusNotApplied(
                businessId,
                operatorId,
                NayaxConnectionMigrationChange.None,
                existing,
                existing.CredentialRevision);
        }

        return null;
    }

    private Task<bool> ApplyReadyAsync(
        INayaxConnectionStore store,
        int credentialRevision,
        CancellationToken cancellationToken) =>
        store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(credentialRevision, NayaxConnectionStatus.Ready, _clock.UtcNow),
            cancellationToken);

    private static NayaxConnectionMigrationResult StatusNotApplied(
        int businessId,
        string operatorId,
        NayaxConnectionMigrationChange change,
        NayaxConnection? existing,
        int expectedRevision) =>
        new()
        {
            Outcome = NayaxConnectionMigrationOutcome.StatusNotApplied,
            DryRun = false,
            Change = change,
            BusinessId = businessId,
            ConfiguredOperatorId = operatorId,
            StatusBefore = existing?.Status,
            CredentialRevisionBefore = existing?.CredentialRevision,
            Message =
                $"The connection was not marked Ready: the stored credential revision is no longer "
                    + $"{expectedRevision.ToString(CultureInfo.InvariantCulture)}, so something else "
                    + "saved a credential while this command ran and the status result was discarded "
                    + "rather than describing credentials it was not produced for. Re-run the "
                    + "command after confirming what is stored.",
        };

    private static string DryRunMessage(NayaxConnectionMigrationChange plan) =>
        plan switch
        {
            NayaxConnectionMigrationChange.CredentialsStored =>
                "Dry run: nothing was written. An apply would store the configured operator id and "
                    + "access token for this business, encrypted, and mark the connection Ready.",
            NayaxConnectionMigrationChange.StatusMarkedReady =>
                "Dry run: nothing was written. This business already holds exactly the configured "
                    + "operator id and access token, so an apply would only mark the connection "
                    + "Ready; the stored credential would not be re-saved and its revision would not "
                    + "move.",
            _ =>
                "Dry run: nothing was written, and an apply would write nothing either. This business "
                    + "already holds exactly the configured operator id and access token, marked "
                    + "Ready.",
        };

    private static string AppliedMessage(NayaxConnectionMigrationChange change) =>
        change switch
        {
            NayaxConnectionMigrationChange.CredentialsStored =>
                "Applied: the configured operator id and access token are now stored for this "
                    + "business, encrypted, and the connection is marked Ready. The global NayaxLynx "
                    + "settings are deliberately unchanged - removing them is a separate human step, "
                    + "after the Nayax client reads the per-business record (issue #520).",
            NayaxConnectionMigrationChange.StatusMarkedReady =>
                "Applied: this business already held exactly the configured operator id and access "
                    + "token, so only the status was written and the connection is now marked Ready. "
                    + "The stored credential was not re-saved and its revision did not move.",
            _ =>
                "Nothing to do: this business already holds exactly the configured operator id and "
                    + "access token, marked Ready. Re-running this command changes nothing.",
        };

    /// <summary>
    /// Compares two access tokens without branching on their contents. The operator running this
    /// command already knows both values, so this is hygiene rather than a defence - but a token
    /// comparison that short-circuits is the kind of detail that gets copied into a place where it
    /// does matter.
    /// </summary>
    private static bool TokensMatch(string stored, string configured)
    {
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);

        try
        {
            return CryptographicOperations.FixedTimeEquals(storedBytes, configuredBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBytes);
            CryptographicOperations.ZeroMemory(configuredBytes);
        }
    }

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static NayaxConnectionMigrationResult Refusal(
        NayaxConnectionMigrationOutcome outcome,
        bool dryRun,
        string message,
        int? businessId = null,
        string? configuredOperatorId = null) =>
        new()
        {
            Outcome = outcome,
            DryRun = dryRun,
            Message = message,
            BusinessId = businessId,
            ConfiguredOperatorId = configuredOperatorId,
        };
}
