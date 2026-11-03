using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Gst;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Products;

/// <summary>
/// Configures one GST rule on every product of the caller's business that carries none (issue
/// #1076), and only ever exactly that.
///
/// <para><strong>Configuration, not classification.</strong> It writes the rule column of the
/// eligible products and nothing else: no purchase, purchase line, delivery or package charge, GST
/// classification, provenance, unit cost, AVCO, costing quantity, inventory value, stock movement,
/// sale, supplier rule or stored document is touched, and no product is created, retired or
/// deleted. Nothing is reclassified retroactively either - a rule reaches already-recorded purchase
/// data only through the Admin Preview/Apply maintenance workflow (issue #433), exactly as it does
/// when one product's rule is set on its own resource (AGENTS.md § Product GST rules and supplier
/// defaults).</para>
///
/// <para><strong>Validation first, then the write, then the audit.</strong>
/// <see cref="BulkProductGstRulePolicy"/> refuses an unsupported target and a request carrying no
/// confirmed count before the database is reached at all, so a refused request reads and writes
/// nothing. The write is one transaction whose authoritative count, comparison against the
/// confirmed count and update are one operation (<see cref="IBulkProductGstRuleStore"/>). The audit
/// event is recorded only after the write committed and only when it actually changed something,
/// because the event is evidence about a change rather than about an attempt.</para>
///
/// <para><strong>Which business is never asked.</strong> The request carries the rule and a count
/// and nothing else; the business comes from the authenticated actor's membership through
/// <see cref="ICurrentBusinessProvider"/> and the rows are reached through the central tenant query
/// filters, so no route, query, body or header value can aim this at another business. Which
/// <em>roles</em> may call it is the endpoint's existing <c>Inventory.Operate</c> capability - the
/// same one the single-product GST rule resource uses - decided by the authorization middleware
/// before this runs, and a read-only support session and a role preview are refused by their own
/// central method rules because the endpoint is a <c>POST</c>.</para>
///
/// <para><strong>An unauditable change is not made.</strong> The acting identity is read
/// <em>before</em> the write, and a request with no identified actor is refused rather than stored
/// with an anonymous audit event - the ordering <c>UpdateBusinessSettings</c> uses, and for the
/// same reason.</para>
/// </summary>
public sealed class SetBulkProductGstRule
{
    private readonly IBulkProductGstRuleStore _store;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IBulkProductGstRuleAudit _audit;
    private readonly IClock _clock;

    public SetBulkProductGstRule(
        IBulkProductGstRuleStore store,
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actors,
        IBulkProductGstRuleAudit audit,
        IClock clock)
    {
        _store = store;
        _currentBusiness = currentBusiness;
        _actors = actors;
        _audit = audit;
        _clock = clock;
    }

    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// The target rule is not one of the two a bulk change may set, or the request carried no
    /// confirmed eligible count. Nothing is written.
    /// </exception>
    /// <exception cref="Inventory.Domain.Exceptions.DomainConflictException">
    /// The eligible count changed between the operator's confirmation and the write. Nothing is
    /// written or audited.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, or no identified actor the change could be recorded
    /// against. Nothing is written in either case.
    /// </exception>
    public async Task<BulkProductGstRuleApplied> Handle(
        SetBulkProductGstRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = BulkProductGstRulePolicy.RequireTarget(request.GstRule);
        var expectedEligibleCount =
            BulkProductGstRulePolicy.RequireSubmittedEligibleCount(request.ExpectedEligibleCount);

        var businessId = await _currentBusiness
            .RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var actorResult = _actors.GetCurrentActor();
        if (actorResult.Actor is not { } actor)
        {
            throw new BusinessAccessDeniedException(
                actorResult.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
        }

        var changedCount = await _store
            .SetRuleOnProductsWithNoRuleAsync(rule, expectedEligibleCount, cancellationToken)
            .ConfigureAwait(false);

        if (changedCount > 0)
        {
            _audit.Record(new BulkProductGstRuleAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                businessId.Value,
                rule,
                changedCount,
                _clock.UtcNow));
        }

        return new BulkProductGstRuleApplied(rule, changedCount);
    }
}
