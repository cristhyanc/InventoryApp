namespace Inventory.Application.Subscriptions;

/// <summary>
/// The grandfathering step's own record (issue #966): the fixed cutoff and whether the step
/// finished.
/// </summary>
/// <param name="CutoffUtc">
/// The instant eligibility is decided against, fixed by the first run and never moved. Businesses
/// created before it, and trading at it, are grandfathered; everything created at or after it starts
/// on Free. The first run takes it from the application clock, so it is also when the step began.
/// </param>
/// <param name="CompletedAtUtc">
/// When a run finished granting every eligible business, or <see langword="null"/> while none has.
/// <strong>This is the completion marker enforcement waits for</strong>: while it is absent, every
/// business is unrestricted however the rollout setting is set.
/// </param>
/// <param name="GrantedBusinessCount">
/// How many eligible businesses held the grandfathered grant when the run completed - the ones that
/// run granted plus the ones that already held one, so a resumed run records the whole picture
/// rather than only its own share. Operational evidence about the step; the entitlement rule never
/// reads it.
/// </param>
public sealed record SubscriptionGrandfatheringState(
    DateTime CutoffUtc,
    DateTime? CompletedAtUtc,
    int GrantedBusinessCount)
{
    /// <summary>Whether the completion marker exists.</summary>
    public bool IsComplete => CompletedAtUtc is not null;
}

/// <summary>
/// The narrow persistence port over the grandfathering record (issue #966), owned by the Application
/// layer.
///
/// <para><strong>One row, about no business at all.</strong> The cutoff and the completion marker are
/// one answer for the whole application - the step runs once, over every business - so the row
/// carries no business key, exactly as the option-visibility setting of issue #873 does. It is a
/// platform table; see docs/architecture.md § Business subscriptions and plan entitlements.</para>
///
/// <para><strong>Three methods, and none of them moves the cutoff.</strong> A first run records it,
/// a later run reuses it, and a completion stamps the marker. There is no method that sets, clears
/// or recomputes a cutoff, and no method takes a predicate, a column list, a page or a statement:
/// the one thing this record must never do is start enforcing against a date somebody changed after
/// the grants were written.</para>
///
/// <para><strong>Who reaches it.</strong> The write half is reached only from the human-run
/// grandfathering command. The read half is reached from that command, from the startup check that
/// reports the missing step, and from the per-request entitlement read - because a business's
/// entitlement depends on whether enforcement may start at all.</para>
/// </summary>
public interface ISubscriptionGrandfatheringStore
{
    /// <summary>
    /// The stored record, or <see langword="null"/> when the step has never run - which is the state
    /// every installation is in today, and the state in which no business is restricted.
    /// </summary>
    Task<SubscriptionGrandfatheringState?> FindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="cutoffUtc"/> as the cutoff if no record exists yet, and answers the
    /// record either way.
    ///
    /// <para><strong>An existing cutoff is reused, never moved.</strong> A second, interrupted or
    /// resumed run therefore grants by exactly the instant the first run fixed, which is what makes
    /// "a business created after the cutoff is never grandfathered" true across runs. The
    /// read-and-insert is one operation, so two concurrent first runs cannot record two
    /// cutoffs.</para>
    /// </summary>
    Task<SubscriptionGrandfatheringState> StartAsync(DateTime cutoffUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps the completion marker on the existing record, and answers it.
    ///
    /// <para>Writing it is the last thing a successful run does: until it exists, enforcement stays
    /// off for everybody, so a run that fell over part way through has granted some businesses and
    /// restricted none. Completing an already-completed record leaves the recorded completion
    /// instant and count as they are, because a rerun that granted nothing finished nothing
    /// new.</para>
    /// </summary>
    /// <param name="completedAtUtc">When the run finished, from the application clock.</param>
    /// <param name="grantedBusinessCount">
    /// How many eligible businesses hold the grandfathered grant as this run leaves them, for the
    /// record's operational evidence.
    /// </param>
    /// <param name="cancellationToken">The command's own token.</param>
    /// <exception cref="InvalidOperationException">
    /// No record exists. A completion marker with no cutoff beside it would be a claim that a step
    /// nobody started has finished.
    /// </exception>
    Task<SubscriptionGrandfatheringState> CompleteAsync(
        DateTime completedAtUtc,
        int grantedBusinessCount,
        CancellationToken cancellationToken);
}
