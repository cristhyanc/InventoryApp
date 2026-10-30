using System.Diagnostics;
using Inventory.Application.PriceCatalogue;

namespace InventoryApi.Adapters.PriceCatalogue;

/// <summary>
/// Writes the Super Admin price-catalogue review audit trail as one structured <c>ILogger</c> event
/// per committed decision (issue #928).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <c>LoggingSupplierPresetAudit</c> and the two platform-business audits beside it: everything on
/// the event comes from the Application layer, and the one thing this adapter adds is the
/// request/correlation id, which is a transport fact.</para>
///
/// <para><strong>A price exception is a second event, not a field on the first.</strong> The issue
/// requires the exception to be audited separately from the listing decision, because it is a
/// separate decision that overrides a financial safeguard: the trail therefore carries
/// <c>ListingConfirmed</c> and, beside it,
/// <see cref="PriceCatalogueReviewAction.PriceExceptionApproved"/> - two events a search can count
/// independently.</para>
///
/// <para><strong>What is deliberately absent.</strong> Any business id, business name or count of
/// businesses; any member's name or address; the supplier's name, the listing's title and its URL;
/// and the accepted price itself. <see cref="PriceCatalogueReviewAuditEntry"/> has no field for any of
/// them, so even a sink that printed the whole entry could not emit one - the same restraint the
/// platform-admin audits beside it show. What it does carry is who decided, which row they decided,
/// which decision it was, whether a price was accepted and the pack quantity they confirmed: all of
/// them identifiers or states, and the last of them the one value this review settles that a price is
/// afterwards divided by.</para>
///
/// <para>Emitted at <c>Information</c>, once per decision, after the write is committed. A refused
/// decision is not audited, because nothing happened. Destination and retention are the host's
/// logging pipeline, which is platform configuration a human must verify rather than something this
/// application can assert.</para>
/// </summary>
public sealed class LoggingPriceCatalogueReviewAudit : IPriceCatalogueReviewAudit
{
    private readonly ILogger<LoggingPriceCatalogueReviewAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPriceCatalogueReviewAudit(
        ILogger<LoggingPriceCatalogueReviewAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PriceCatalogueReviewAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Price catalogue review audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; action "
                + "{Action}; listing {SupplierListingId}; master product {MasterProductId}; "
                + "suggestion {SuggestionId}; waiting key {WaitingKey}; price accepted "
                + "{PriceAccepted}; units per pack {ConfirmedUnitsPerPack}; occurred "
                + "{OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.Action,
            entry.SupplierListingId,
            entry.MasterProductId,
            entry.SuggestionId,
            entry.WaitingKey,
            entry.PriceAccepted,
            entry.ConfirmedUnitsPerPack,
            entry.OccurredAtUtc,
            CorrelationId());
    }

    /// <summary>
    /// The distributed trace id when telemetry is running, and the host's own request identifier
    /// otherwise, so the event is always correlatable to the request that produced it.
    /// </summary>
    private string CorrelationId() =>
        Activity.Current?.Id
            ?? _httpContextAccessor.HttpContext?.TraceIdentifier
            ?? "unknown";
}
