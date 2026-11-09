namespace Inventory.Application.PlatformOptions;

/// <summary>
/// One stored answer about one menu destination (issue #1148): a row exists only because somebody
/// switched that destination, so a key with no row is not represented here at all.
/// </summary>
/// <param name="Key">The catalogue key the row was stored for; see <see cref="MenuVisibilityKeys"/>.</param>
/// <param name="Visible">Whether that destination is offered to members who are not the platform administrator.</param>
public sealed record StoredMenuVisibility(string Key, bool Visible);

/// <summary>
/// The narrow persistence port over the shared menu visibility (issue #1148), owned by the
/// Application layer. It replaces issue #873's single-row <c>IOptionVisibilityStore</c>.
///
/// <para><strong>Two methods, and nothing else.</strong> Like <c>ISupplierPresetStore</c> beside it,
/// no method here takes a predicate, a column list, a page, a sort or a statement, and nothing
/// tenant-owned is readable or writable through it: the setting is about no business at all. There
/// is no delete - switching a destination back on is a write of <see langword="true"/> rather than a
/// removal, so what the application last decided stays readable - and there is no "write them all"
/// method, because a write names exactly one key.</para>
///
/// <para><strong>Who reaches it.</strong> The write is reached only from the Super Admin surface,
/// which carries a denied business scope. The read is reached from that surface <em>and</em> from
/// <c>GET /api/me/access</c>, because a member has to be told which destinations the application
/// still offers them; that read returns these rows and nothing else. See docs/architecture.md
/// § Switchable Admin menu visibility.</para>
/// </summary>
public interface IMenuVisibilityStore
{
    /// <summary>
    /// Every stored answer, in no guaranteed order. A key with no row is absent rather than reported
    /// as offered: "no answer stored" and "this is the answer" are different facts, and only
    /// <see cref="GetMenuVisibility"/> may turn the first into the second.
    /// </summary>
    Task<IReadOnlyList<StoredMenuVisibility>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores whether the destination <paramref name="key"/> names is offered, and answers the state
    /// as it was stored. It writes that one key's row and touches no other: a second write to a
    /// different key, made at the same time, cannot overwrite this one.
    /// </summary>
    /// <param name="key">A catalogue key, already checked against <see cref="MenuVisibilityCatalogue"/>.</param>
    /// <param name="visible">The state the destination is to end in.</param>
    /// <param name="updatedAtUtc">When the change was made, from the application clock.</param>
    /// <param name="cancellationToken">The request's own token.</param>
    Task<bool> SetAsync(
        string key,
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken);
}
