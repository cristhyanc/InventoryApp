namespace Inventory.Domain.ConnectedMachines;

/// <summary>
/// One day's recorded connected-machine count for one business, in one account series (issue #978).
///
/// <para><strong>It is billing evidence, so it is append-only.</strong> A snapshot says what the
/// business's Nayax account held on a UTC day; a later day is a later snapshot, never an edit of this
/// one. There is no update, delete or correction path anywhere in the application - the persistence
/// port offers none and the persistence boundary refuses one - because #969 bills from these rows and
/// a figure that can change after it was charged is not evidence.</para>
///
/// <para><strong>At most one snapshot per business per UTC day per account series</strong>, which is
/// the database's own rule (a unique index), not a <c>WHERE</c> clause two concurrent reads can both
/// pass. The day is a UTC day deliberately, and not the business's own reporting day: an allowance is
/// a billing fact about an account rather than a trading day, and every business's snapshots have to
/// be comparable on one calendar.</para>
/// </summary>
/// <param name="DateUtc">
/// The UTC day this count is for, at midnight. <see cref="ConnectedMachineSnapshotDay"/> is the one
/// place an instant becomes this value.
/// </param>
/// <param name="AccountSeries">
/// Which Nayax account this count belongs to, as the business's own internal connection series (see
/// <see cref="ConnectedMachineSnapshotPolicy"/>). Never the operator id, a token or any part of a
/// credential.
/// </param>
/// <param name="MachineCount">
/// How many connected machines the account held: the number of ids in
/// <paramref name="ProviderMachineIds"/>, recorded beside them so a stored count never has to be
/// recomputed from text to be read.
/// </param>
/// <param name="ProviderMachineIds">
/// The provider machine ids counted, so a count months old can still be explained. They are the
/// provider's own ids, which are not secrets and name no credential.
/// </param>
/// <param name="TakenAtUtc">The instant the read this count came from was taken.</param>
/// <param name="ReadWasComplete">
/// Whether the fleet read behind it accounted for the whole fleet. It is always
/// <see langword="true"/> on a stored row, because an incomplete read stores nothing at all; it is
/// recorded rather than assumed so that a future decision to store partial evidence could not be
/// made invisibly - every row says what it was taken from.
/// </param>
public sealed record ConnectedMachineSnapshot(
    DateTime DateUtc,
    int AccountSeries,
    int MachineCount,
    IReadOnlyList<long> ProviderMachineIds,
    DateTime TakenAtUtc,
    bool ReadWasComplete);

/// <summary>
/// The one place an instant becomes the UTC day a snapshot belongs to (issue #978).
/// </summary>
public static class ConnectedMachineSnapshotDay
{
    /// <summary>
    /// The UTC day <paramref name="instantUtc"/> falls on, at midnight and marked UTC.
    ///
    /// <para>Stated once because the daily claim, the job's "already counted today" check and the
    /// staleness age all have to mean the same day boundary; two of them disagreeing would either
    /// store two snapshots for one day or none.</para>
    /// </summary>
    public static DateTime For(DateTime instantUtc) =>
        DateTime.SpecifyKind(instantUtc.Date, DateTimeKind.Utc);
}
