namespace Inventory.Domain.Tenancy;

/// <summary>
/// The declared <see cref="BusinessStatus"/> vocabulary, and the check a value has to pass before
/// it is stored as a business's lifecycle status (issue #584).
///
/// It is the status counterpart of <see cref="BusinessRoles"/>, and it exists for the mirror-image
/// reason. <see cref="BusinessRoles"/> guards a value arriving from *outside* the code on its way
/// into an access decision; this guards a value arriving from *inside* the code on its way into the
/// database. A C# enum is a compile-time constraint, not a validation: a write path that never
/// assigned <c>Business.Status</c> hands over <c>0</c>, a cast hands over whatever integer was
/// cast, and neither is a status anybody decided. The fail-closed answer is the same in
/// both directions - refuse it - because the alternative is a business row whose access state
/// nobody chose.
/// </summary>
public static class BusinessStatuses
{
    /// <summary>
    /// The declared statuses, in lifecycle order. Written out rather than enumerated from the
    /// stored values so a status added out of numeric order still reads in the order a business
    /// actually moves through.
    /// </summary>
    public static IReadOnlyList<BusinessStatus> All { get; } =
    [
        BusinessStatus.Pending,
        BusinessStatus.Active,
        BusinessStatus.Rejected,
        BusinessStatus.Deactivated,
    ];

    /// <summary>
    /// The message an undeclared status is refused with. Built from the enum's own member names so
    /// it cannot drift from the vocabulary it describes, and it names only the rule: the value that
    /// failed it belongs in the exception the caller of this type raises, never in text a request
    /// could be answered with.
    /// </summary>
    public static string UnsupportedMessage { get; } =
        $"A business lifecycle status must be one of {string.Join(", ", Enum.GetNames<BusinessStatus>())}, "
            + "stated deliberately by whichever path writes the business.";

    /// <summary>Whether <paramref name="status"/> is one of the declared statuses.</summary>
    public static bool IsSupported(BusinessStatus status) => Enum.IsDefined(status);
}
