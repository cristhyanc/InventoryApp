using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one answer to "is this collector run still open?" (issue #937, shared with issue #941's
/// matches endpoint).
///
/// <para><strong>Touching an overdue run expires it, and then refuses the request.</strong> That is
/// issue #937's own wording - "expired the next time it is touched" - and the order matters: the
/// expiry is committed so the run's unanswered searches are recorded as <c>NotSearched</c> rather than
/// left open until something else notices, and only then is the caller told the run is closed. A run
/// that was already finished is refused without being touched.</para>
///
/// <para>It is a shared helper rather than a method on one adapter because every collector endpoint
/// that writes has to ask the same question before it writes anything, and two copies of "closed,
/// overdue or open" would be two answers to it. The commit is passed in because the caller owns the
/// transaction: the results endpoint opens an ordinary one and the matches endpoint opens the
/// <c>BEGIN IMMEDIATE</c> one it needs.</para>
/// </summary>
internal static class PriceCollectorOpenRun
{
    /// <summary>
    /// The run this request is about while it is still open, and <see langword="null"/> when no run
    /// has that id.
    /// </summary>
    /// <param name="db">The request's own context, already inside the caller's transaction.</param>
    /// <param name="runId">The run the collector is reporting under.</param>
    /// <param name="asOfUtc">Now, from the application clock.</param>
    /// <param name="commitExpiry">
    /// The caller's own commit, used for the one case this helper writes: an overdue run, whose expiry
    /// is committed before the refusal is thrown.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="DomainConflictException">
    /// The run has finished, or was overdue and has just been expired. Nothing else is stored.
    /// </exception>
    public static async Task<PriceCollectorRun?> FindAsync(
        AppDbContext db,
        long runId,
        DateTime asOfUtc,
        Func<CancellationToken, Task> commitExpiry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(commitExpiry);

        var run = await db.PriceCollectorRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        if (PriceCollectorRunPolicy.IsFinished(run.Status))
        {
            throw new DomainConflictException(PriceCollectorRunPolicy.AlreadyFinishedMessage);
        }

        if (run.ExpiresAtUtc is { } expiresAtUtc
            && PriceCollectorRunPolicy.HasExpired(expiresAtUtc, asOfUtc))
        {
            await PriceCollectorRunClosure
                .CloseAsync(db, run, asOfUtc, PriceCollectorRunStatus.Expired, cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await commitExpiry(cancellationToken).ConfigureAwait(false);

            throw new DomainConflictException(PriceCollectorRunPolicy.AlreadyFinishedMessage);
        }

        return run;
    }
}
