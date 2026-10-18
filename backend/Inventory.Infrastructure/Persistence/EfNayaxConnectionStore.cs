using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxConnectionStore"/> (issue #518). It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// <see cref="BusinessNayaxConnection"/> entity it reads, like every other EF adapter behind an
/// Application persistence port (docs/architecture.md § Inventory.Infrastructure).
///
/// It adds no business predicate of its own: reads go through the central
/// <see cref="AppDbContext"/> query filter and the insert is stamped by
/// <c>BusinessOwnershipEnforcer</c> on save, so one business can never read, decrypt or overwrite
/// another's credentials. The conditional status write is the one statement that needs saying out
/// loud, because <c>ExecuteUpdate</c> runs in the database and does not pass through
/// <c>SaveChanges</c>: its <c>WHERE</c> clause still carries the owning business, because EF
/// applies the same query filter to the statement's source query, and this adapter additionally
/// refuses to run at all without a single resolved business - an unscoped context would otherwise
/// be the one way an <c>UPDATE</c> here could reach every business's row.
///
/// The one row outside <c>BusinessNayaxConnections</c> it writes is the business's own
/// <c>SalesProvider</c>, and only while that is empty (issue #759): storing a provider's credentials
/// is what records that the business sells through it, so the two belong in one transaction rather
/// than in two callers that both have to remember. See <see cref="ClaimSalesProviderAsync"/>.
///
/// Encrypting and decrypting the token is <see cref="INayaxTokenProtector"/>'s job. This adapter
/// never writes a plaintext token to the database and never logs one: the only values its log event
/// carries are the business id and the two credential revisions.
///
/// <para><strong>It is the one adapter a request reaches from several places at once, so it owns
/// access to the context it shares (issue #667).</strong> Since issue #520 every ordinary Nayax call
/// resolves the business's credentials through this store, and a Nayax <c>401</c> writes a status
/// back through it - so a bounded fan-out of Nayax requests (the reorder and Pick List fan-outs, the
/// per-site machine reads, the catalogue import) puts several reads, and several conditional writes,
/// on the one scoped <see cref="AppDbContext"/> of that request, which supports exactly one
/// operation at a time. <see cref="_contextGate"/> admits one of them at a time. It serializes the
/// *database* work only: each caller resolves its own snapshot, the HTTP requests the callers go on
/// to make still overlap, and a cancelled caller stops waiting rather than queuing behind the
/// others. The gate is per request, because this adapter is registered scoped beside the context it
/// guards, and it is deliberately not re-entrant: every public member takes it exactly once and the
/// private cores below it take it never.</para>
///
/// <para>It is not a substitute for the rule in docs/architecture.md § Concurrency inside one
/// request. It makes the Nayax credential path safe against itself; a use case that starts other
/// scoped persistence work while a Nayax call is in flight still has two operations on one context,
/// and must await them one at a time as it always had to.</para>
/// </summary>
public sealed class EfNayaxConnectionStore : INayaxConnectionStore, IDisposable
{
    private readonly AppDbContext _db;
    private readonly INayaxTokenProtector _protector;
    private readonly IClock _clock;
    private readonly ILogger<EfNayaxConnectionStore> _logger;

    /// <summary>
    /// Admits one operation at a time to the request's shared <see cref="AppDbContext"/>. See the
    /// type remarks: held only for the database work, never re-entered, and released on every path.
    /// </summary>
    private readonly SemaphoreSlim _contextGate = new(1, 1);

    public EfNayaxConnectionStore(
        AppDbContext db,
        INayaxTokenProtector protector,
        IClock clock,
        ILogger<EfNayaxConnectionStore> logger)
    {
        _db = db;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Releases the context gate. The container disposes this adapter with the request scope that
    /// owns it, beside the <see cref="AppDbContext"/> it guards.
    /// </summary>
    public void Dispose() => _contextGate.Dispose();

    /// <inheritdoc />
    public async Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken)
    {
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task<NayaxConnection?> FindCoreAsync(CancellationToken cancellationToken)
    {
        if (_db.CurrentBusinessId is null)
        {
            // No current business means no record of one, whether the caller's membership was
            // denied or the context was deliberately constructed unscoped. Reporting none is the
            // fail-closed answer; reporting "whichever row came first" would not be.
            return null;
        }

        return await _db.BusinessNayaxConnections
            .AsNoTracking()
            .Select(connection => new NayaxConnection(
                connection.OperatorId,
                connection.Status,
                connection.CredentialRevision,
                connection.LastTestedAtUtc,
                connection.UpdatedAtUtc)
            {
                MachineChecksPending = connection.MachineChecksPending,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken)
    {
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindCredentialCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task<NayaxConnectionCredential?> FindCredentialCoreAsync(CancellationToken cancellationToken)
    {
        if (_db.CurrentBusinessId is not { } businessId)
        {
            return null;
        }

        var stored = await _db.BusinessNayaxConnections
            .AsNoTracking()
            .Select(connection => new
            {
                connection.OperatorId,
                connection.AccessTokenCiphertext,
                connection.EncryptionKeyId,
                connection.CredentialRevision,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // Throws NayaxTokenProtectionException when the ciphertext is corrupt, its key is not
        // configured, or - since issue #681 - it was encrypted for a different business than the row
        // it was read from. Deliberately not caught: an undecryptable credential must not look like
        // an absent or empty one. The business id comes from the context's own tenancy scope, which
        // is the same scope whose query filter selected the row, so the value the ciphertext is
        // authenticated against is never one a request supplied.
        var accessToken = _protector.Unprotect(
            new ProtectedNayaxToken(stored.EncryptionKeyId, stored.AccessTokenCiphertext),
            BusinessId.From(businessId));

        return new NayaxConnectionCredential(stored.OperatorId, accessToken, stored.CredentialRevision);
    }

    /// <inheritdoc />
    public async Task<NayaxConnectionSnapshot?> FindForOperationAsync(
        Func<NayaxConnectionStatus, bool> mayDecryptToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mayDecryptToken);

        // The read every call of a Nayax fan-out makes, which is why the gate matters most here.
        // Each caller still gets its own snapshot of the row as it stood when its own turn came:
        // waiting is not caching, and the status a call is gated on is still the status its token
        // was read beside.
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FindForOperationCoreAsync(mayDecryptToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task<NayaxConnectionSnapshot?> FindForOperationCoreAsync(
        Func<NayaxConnectionStatus, bool> mayDecryptToken,
        CancellationToken cancellationToken)
    {
        if (_db.CurrentBusinessId is not { } businessId)
        {
            return null;
        }

        // One statement for the status, the operator id, the ciphertext and the revision. Two
        // queries would let a credential save commit between them and hand the caller the previous
        // status beside the new token - a pair the row never held.
        var stored = await _db.BusinessNayaxConnections
            .AsNoTracking()
            .Select(connection => new
            {
                connection.OperatorId,
                connection.Status,
                connection.AccessTokenCiphertext,
                connection.EncryptionKeyId,
                connection.CredentialRevision,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        if (!mayDecryptToken(stored.Status))
        {
            // The caller's gate refused this snapshot's own status, so the ciphertext is left
            // alone: a refused state never handles the secret at all.
            return new NayaxConnectionSnapshot(stored.Status, credential: null);
        }

        // Throws for a corrupt ciphertext, an unconfigured key or a ciphertext encrypted for
        // another business, exactly as FindCredentialAsync does, and for the same reason: an
        // undecryptable credential must not look like an absent one.
        var accessToken = _protector.Unprotect(
            new ProtectedNayaxToken(stored.EncryptionKeyId, stored.AccessTokenCiphertext),
            BusinessId.From(businessId));

        return new NayaxConnectionSnapshot(
            stored.Status,
            new NayaxConnectionCredential(stored.OperatorId, accessToken, stored.CredentialRevision));
    }

    /// <inheritdoc />
    public async Task<NayaxConnection> SaveCredentialAsync(
        string operatorId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCredentialCoreAsync(operatorId, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    /// <summary>
    /// The save itself, running with the context gate already held: its read-back goes to
    /// <see cref="FindCoreAsync"/> rather than to the public <see cref="FindAsync"/>, because the
    /// gate is not re-entrant and a save that waited for itself would never finish.
    /// </summary>
    private async Task<NayaxConnection> SaveCredentialCoreAsync(
        string operatorId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var businessId = RequireCurrentBusinessId("saved");

        // The read-only support-session rule, for the ExecuteUpdate below that never reaches
        // BusinessOwnershipEnforcer (issue #681).
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        // Encrypted before anything is written, so a key-configuration failure stores nothing at
        // all rather than a row with an empty token. The ciphertext is bound to this business
        // (issue #681), so the value this save writes cannot later be decrypted from another
        // business's row.
        var protectedToken = _protector.Protect(accessToken, BusinessId.From(businessId));
        var savedAt = _clock.UtcNow;

        // A caller that already opened a transaction on this context owns the commit, and this save
        // joins it rather than starting a second one - which is how the issue-#519 migration command
        // makes storing the credential and marking the connection Ready one atomic change. With no
        // ambient transaction the save still gets its own, so a save on its own is unchanged.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        // Inside the transaction, and before the credential is written: storing a provider's
        // credentials is what records that the business sells through it, and a save refused because
        // the business already sells through a different provider must leave nothing behind
        // (issue #759).
        await ClaimSalesProviderAsync(businessId, cancellationToken).ConfigureAwait(false);

        // The update comes first, and increments the revision in the database rather than from a
        // value this process read, so two overlapping saves both count and the row that remains is
        // wholly the one that committed last - never a mix of the two.
        var updated = await _db.BusinessNayaxConnections
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.OperatorId, operatorId)
                    .SetProperty(connection => connection.AccessTokenCiphertext, protectedToken.Ciphertext)
                    .SetProperty(connection => connection.EncryptionKeyId, protectedToken.KeyId)
                    .SetProperty(connection => connection.Status, NayaxConnectionStatus.PendingPermissions)
                    .SetProperty(connection => connection.LastTestedAtUtc, (DateTime?)null)
                    // Cleared for the same reason the last-tested instant is: the machine checks
                    // that were or were not verified belong to the token being replaced, and a new
                    // token must inherit none of the previous one's test facts. A save from a tested
                    // draft writes the real value immediately afterwards, in the same transaction.
                    .SetProperty(connection => connection.MachineChecksPending, false)
                    .SetProperty(connection => connection.UpdatedAtUtc, savedAt)
                    .SetProperty(
                        connection => connection.CredentialRevision,
                        connection => connection.CredentialRevision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        var saved = updated == 0
            ? await InsertAsync(operatorId, protectedToken, savedAt, cancellationToken).ConfigureAwait(false)
            : await ReadBackAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>
    /// Records that this business sells through Nayax, while it has no sales provider yet, and
    /// refuses the save when it already has a different one (issue #759).
    ///
    /// <para><strong>The claim and the comparison are one conditional statement plus the read of its
    /// result</strong>, not a read followed by a decision: the <c>UPDATE</c> carries
    /// <c>SalesProvider IS NULL</c> in its <c>WHERE</c> clause, so it can only ever fill an empty
    /// column and two simultaneous first saves end as one claim. What the row holds afterwards is
    /// then read back and judged by the one Domain rule
    /// (<see cref="SalesProviderPolicy.MayStoreCredentialsFor"/>) - so a provider another caller
    /// claimed a moment ago refuses this save rather than being overwritten by it. Nothing here can
    /// change a provider that is set, which is what makes the column write-once
    /// (<see cref="BusinessSalesProviderEnforcer"/> covers the tracked-entity path for the same
    /// reason).</para>
    ///
    /// <para><c>Businesses</c> is not tenant-owned and carries no query filter, so the business
    /// predicate is written out here. The id is the context's own resolved scope - the same value the
    /// credential row is stamped with - and never one a request supplied.</para>
    /// </summary>
    /// <exception cref="DomainConflictException">
    /// The business already sells through another provider, so these credentials are not stored; the
    /// transaction this runs in is left uncommitted and nothing is written.
    /// </exception>
    private async Task ClaimSalesProviderAsync(int businessId, CancellationToken cancellationToken)
    {
        await _db.Businesses
            .Where(business => business.Id == businessId && business.SalesProvider == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    business => business.SalesProvider, ExternalProvider.Nayax),
                cancellationToken)
            .ConfigureAwait(false);

        var salesProvider = await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => business.SalesProvider)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!SalesProviderPolicy.MayStoreCredentialsFor(salesProvider, ExternalProvider.Nayax))
        {
            throw new DomainConflictException(
                SalesProviderPolicy.RefusalMessage(salesProvider!.Value, ExternalProvider.Nayax));
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryApplyStatusResultAsync(
        NayaxConnectionStatusResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The write a Nayax 401 makes, which in a fan-out arrives several times at once and beside
        // the other calls' reads. Serializing it changes nothing about which result wins: the
        // conditional update is still what discards a result produced for superseded credentials,
        // and it is evaluated against the row as it stands when the statement runs.
        await _contextGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryApplyStatusResultCoreAsync(result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _contextGate.Release();
        }
    }

    private async Task<bool> TryApplyStatusResultCoreAsync(
        NayaxConnectionStatusResult result,
        CancellationToken cancellationToken)
    {
        var businessId = RequireCurrentBusinessId("updated");

        // As above (issue #681): the conditional ExecuteUpdate below bypasses SaveChanges, so the
        // support session's read-only rule is applied here.
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        // One statement: the expected-revision comparison and the write cannot be separated, so a
        // credential save committed after the test started cannot be overwritten by its result.
        // The owning business is in the same WHERE clause, contributed by the central query filter
        // rather than by a predicate written here.
        var matching = _db.BusinessNayaxConnections
            .Where(connection => connection.CredentialRevision == result.CredentialRevision);
        var updatedAt = _clock.UtcNow;

        // Two statements rather than one with a conditional setter, because `ExecuteUpdate` takes an
        // expression that cannot be composed at runtime. Which one runs is the difference between a
        // result that ran the machine checks and one that says nothing about them (issue #537): a
        // 401 from an ordinary call must not claim those checks passed, so it leaves the column
        // exactly as it is. The revision predicate - the part that makes the write safe - is the
        // same in both.
        var updated = result.MachineChecksPending is { } machineChecksPending
            ? await matching
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(connection => connection.Status, result.Status)
                        .SetProperty(connection => connection.MachineChecksPending, machineChecksPending)
                        .SetProperty(connection => connection.LastTestedAtUtc, result.TestedAtUtc)
                        .SetProperty(connection => connection.UpdatedAtUtc, updatedAt),
                    cancellationToken)
                .ConfigureAwait(false)
            : await matching
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(connection => connection.Status, result.Status)
                        .SetProperty(connection => connection.LastTestedAtUtc, result.TestedAtUtc)
                        .SetProperty(connection => connection.UpdatedAtUtc, updatedAt),
                    cancellationToken)
                .ConfigureAwait(false);

        if (updated > 0)
        {
            return true;
        }

        var storedRevision = await _db.BusinessNayaxConnections
            .AsNoTracking()
            .Select(connection => (int?)connection.CredentialRevision)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Identifiers and revisions only. The status, the operator id and the token stay out of
        // the record of a discarded result.
        _logger.LogWarning(
            "Stale Nayax status result discarded for business {BusinessId}: the result was produced "
                + "for credential revision {ResultCredentialRevision} but the stored revision is "
                + "{StoredCredentialRevision}.",
            businessId,
            result.CredentialRevision,
            storedRevision);

        return false;
    }

    private async Task<NayaxConnection> InsertAsync(
        string operatorId,
        ProtectedNayaxToken protectedToken,
        DateTime savedAt,
        CancellationToken cancellationToken)
    {
        var row = new BusinessNayaxConnection
        {
            OperatorId = operatorId,
            AccessTokenCiphertext = protectedToken.Ciphertext,
            EncryptionKeyId = protectedToken.KeyId,
            Status = NayaxConnectionStatus.PendingPermissions,
            CredentialRevision = 1,
            LastTestedAtUtc = null,
            MachineChecksPending = false,
            UpdatedAtUtc = savedAt,
        };

        // BusinessId is left unset on purpose: BusinessOwnershipEnforcer stamps the caller's
        // business on save, which is also what refuses the write if no business is resolved.
        _db.BusinessNayaxConnections.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new NayaxConnection(
            row.OperatorId,
            row.Status,
            row.CredentialRevision,
            row.LastTestedAtUtc,
            row.UpdatedAtUtc)
        {
            MachineChecksPending = row.MachineChecksPending,
        };
    }

    /// <summary>
    /// Reads back what the conditional update actually wrote, rather than reporting the values this
    /// process intended: the revision is computed in the database, so only the row can say what it
    /// became. It reads through <see cref="FindCoreAsync"/> because the caller already holds the
    /// context gate.
    /// </summary>
    private async Task<NayaxConnection> ReadBackAsync(CancellationToken cancellationToken) =>
        await FindCoreAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The Nayax connection that was just updated for the current business cannot be "
                    + "read back, so the save reports a failure rather than values the database may "
                    + "not hold.");

    /// <summary>
    /// A write needs exactly one resolved business. Both an unresolved caller and a deliberately
    /// unscoped context report none, and neither may write here: <c>ExecuteUpdate</c> runs in the
    /// database without passing through <c>BusinessOwnershipEnforcer</c>, so an unscoped statement
    /// would carry no business predicate and reach every business's row.
    /// </summary>
    private int RequireCurrentBusinessId(string operation) =>
        _db.CurrentBusinessId
            ?? throw new CrossBusinessAccessException(
                $"A Nayax connection cannot be {operation} without a single resolved current "
                    + "business. The caller has no business membership, or the context was "
                    + "constructed unscoped, so the write is refused.");
}
