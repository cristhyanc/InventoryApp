using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
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
/// Encrypting and decrypting the token is <see cref="INayaxTokenProtector"/>'s job. This adapter
/// never writes a plaintext token to the database and never logs one: the only values its log event
/// carries are the business id and the two credential revisions.
/// </summary>
public sealed class EfNayaxConnectionStore : INayaxConnectionStore
{
    private readonly AppDbContext _db;
    private readonly INayaxTokenProtector _protector;
    private readonly IClock _clock;
    private readonly ILogger<EfNayaxConnectionStore> _logger;

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

    /// <inheritdoc />
    public async Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken)
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
                connection.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken)
    {
        if (_db.CurrentBusinessId is null)
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

        // Throws NayaxTokenProtectionException when the ciphertext is corrupt or its key is not
        // configured. Deliberately not caught: an undecryptable credential must not look like an
        // absent or empty one.
        var accessToken = _protector.Unprotect(
            new ProtectedNayaxToken(stored.EncryptionKeyId, stored.AccessTokenCiphertext));

        return new NayaxConnectionCredential(stored.OperatorId, accessToken, stored.CredentialRevision);
    }

    /// <inheritdoc />
    public async Task<NayaxConnectionSnapshot?> FindForOperationAsync(
        Func<NayaxConnectionStatus, bool> mayDecryptToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mayDecryptToken);

        if (_db.CurrentBusinessId is null)
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

        // Throws for a corrupt ciphertext or an unconfigured key, exactly as FindCredentialAsync
        // does, and for the same reason: an undecryptable credential must not look like an absent
        // one.
        var accessToken = _protector.Unprotect(
            new ProtectedNayaxToken(stored.EncryptionKeyId, stored.AccessTokenCiphertext));

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

        RequireCurrentBusinessId("saved");

        // Encrypted before anything is written, so a key-configuration failure stores nothing at
        // all rather than a row with an empty token.
        var protectedToken = _protector.Protect(accessToken);
        var savedAt = _clock.UtcNow;

        // A caller that already opened a transaction on this context owns the commit, and this save
        // joins it rather than starting a second one - which is how the issue-#519 migration command
        // makes storing the credential and marking the connection Ready one atomic change. With no
        // ambient transaction the save still gets its own, so a save on its own is unchanged.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

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

    /// <inheritdoc />
    public async Task<bool> TryApplyStatusResultAsync(
        NayaxConnectionStatusResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var businessId = RequireCurrentBusinessId("updated");

        // One statement: the expected-revision comparison and the write cannot be separated, so a
        // credential save committed after the test started cannot be overwritten by its result.
        // The owning business is in the same WHERE clause, contributed by the central query filter
        // rather than by a predicate written here.
        var updated = await _db.BusinessNayaxConnections
            .Where(connection => connection.CredentialRevision == result.CredentialRevision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, result.Status)
                    .SetProperty(connection => connection.LastTestedAtUtc, result.TestedAtUtc)
                    .SetProperty(connection => connection.UpdatedAtUtc, _clock.UtcNow),
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
            row.UpdatedAtUtc);
    }

    /// <summary>
    /// Reads back what the conditional update actually wrote, rather than reporting the values this
    /// process intended: the revision is computed in the database, so only the row can say what it
    /// became.
    /// </summary>
    private async Task<NayaxConnection> ReadBackAsync(CancellationToken cancellationToken) =>
        await FindAsync(cancellationToken).ConfigureAwait(false)
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
