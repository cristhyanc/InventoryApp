namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// One business having been deactivated, as the hook below is told about it (issue #545).
///
/// <para>It carries the business key and the instant, and nothing else, because nothing else is a
/// fact about the transition: the name, the members and the invites belong to whoever has a reason
/// to read them, and a hook that was handed them would be a cross-business read nobody reviewed.</para>
/// </summary>
/// <param name="BusinessId">The business that was switched off.</param>
/// <param name="DeactivatedAtUtc">The instant the deactivation is being recorded at.</param>
public sealed record BusinessDeactivated(int BusinessId, DateTime DeactivatedAtUtc);

/// <summary>
/// The hook point on the deactivate transition (issue #545's fourth criterion): anything that has to
/// happen when a business is switched off, and that is not the business's own status or its invites.
///
/// <para><strong>Why it exists, and what is behind it now.</strong> Issue #512's support sessions
/// have to end when the business they are inside is deactivated, and the only safe place for that is
/// inside the transaction that deactivates it: a session ended by a deactivation that then rolled
/// back would have been ended for nothing, and a session left running by a deactivation that
/// committed would be exactly the access the deactivation exists to stop. That ordering is a
/// property of this transition, so it was declared here, once, by issue #545 - with nothing behind
/// it - rather than left for a later task to find a place for. Issue #550 is that later task:
/// <c>Inventory.Application.SupportSessions.EndSupportSessionsOnBusinessDeactivation</c> is the
/// implementation the composition root registers, and it ends every active support session of the
/// deactivated business. No session logic is in this transition, and none belongs here.</para>
///
/// <para><strong>Called inside the write transaction, after the status and the invites.</strong>
/// Whatever it writes commits or rolls back with the deactivation, and an implementation that throws
/// refuses the whole deactivation rather than leaving a half-finished one - which is the direction a
/// hook about *stopping* access has to fail in. It is therefore not a notification: it must not block
/// on a remote call, and anything that may fail independently belongs behind its own port with its
/// own retry story.</para>
///
/// <para><strong>Exactly one implementation is registered</strong>, as a real dependency rather than
/// a nullable one. A nullable hook would make "is anything registered?" a question every caller has to
/// ask and one caller will forget, and an optional dependency is how a hook that was supposed to end
/// sessions comes to be silently absent. Issue #545 shipped a no-op here; issue #550 replaced that one
/// registration with the support-session end, which is what the hook was declared for.</para>
/// </summary>
public interface IBusinessDeactivationHook
{
    Task OnBusinessDeactivatedAsync(BusinessDeactivated deactivated, CancellationToken cancellationToken);
}
