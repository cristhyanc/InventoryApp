namespace Inventory.Application.PlatformOptions;

/// <summary>
/// Which of the application's switchable options are offered (issue #873).
///
/// <para>There is exactly one, and it is named rather than keyed: Historical GST Classification,
/// the occasional maintenance tool of issue #433. A named field is deliberate - a general
/// feature-flag framework is explicitly out of scope of that issue, and a map of arbitrary keys is
/// how one starts. A second switchable option would be a second field here, decided by its own
/// issue.</para>
/// </summary>
/// <param name="HistoricalGstClassificationVisible">
/// Whether Historical GST Classification is offered to members who are not the platform
/// administrator. A Super Admin keeps the option in both states.
/// </param>
public sealed record OptionVisibilitySettings(bool HistoricalGstClassificationVisible)
{
    /// <summary>
    /// What every installation that has never switched anything off is offered: everything.
    ///
    /// <para>This is the <em>one</em> statement of the default, and nothing else may restate it. The
    /// stored row is absent until a Super Admin first switches something, so this value is what
    /// <see cref="GetOptionVisibility"/> answers for every existing installation - which is what
    /// makes the setting arrive without changing anything anybody can see.</para>
    /// </summary>
    public static readonly OptionVisibilitySettings Default = new(HistoricalGstClassificationVisible: true);
}

/// <summary>
/// The narrow persistence port over the shared option-visibility setting (issue #873), owned by the
/// Application layer.
///
/// <para><strong>One row, two methods, and nothing else.</strong> Like <c>ISupplierPresetStore</c>
/// beside it, no method here takes a predicate, a column list, a page, a sort or a statement, and
/// nothing tenant-owned is readable or writable through it: the setting is about no business at all.
/// There is no delete - the shipped state is the absence of the row, and switching the option back
/// on is a write of <see langword="true"/> rather than a removal, so what the application last
/// decided stays readable.</para>
///
/// <para><strong>Who reaches it.</strong> The write is reached only from the Super Admin surface,
/// which carries a denied business scope. The read is reached from that surface <em>and</em> from
/// <c>GET /api/me/access</c>, because a member has to be told what the application offers them; that
/// read returns this one setting and nothing else. See docs/architecture.md § Switchable Admin
/// option visibility.</para>
/// </summary>
public interface IOptionVisibilityStore
{
    /// <summary>
    /// The stored setting, or <see langword="null"/> when nothing has ever been switched - which is
    /// the state every installation starts in and is answered as
    /// <see cref="OptionVisibilitySettings.Default"/> by <see cref="GetOptionVisibility"/>. The
    /// adapter does not substitute the default itself: "no answer stored" and "this is the answer"
    /// are different facts, and only one place may turn the first into the second.
    /// </summary>
    Task<OptionVisibilitySettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores whether Historical GST Classification is offered, and answers the setting as it was
    /// stored.
    /// </summary>
    /// <param name="visible">The state the setting is to end in.</param>
    /// <param name="updatedAtUtc">When the change was made, from the application clock.</param>
    /// <param name="cancellationToken">The request's own token.</param>
    Task<OptionVisibilitySettings> SetHistoricalGstClassificationVisibleAsync(
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken);
}
