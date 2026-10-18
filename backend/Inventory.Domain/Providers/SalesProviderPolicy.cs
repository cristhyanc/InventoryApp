namespace Inventory.Domain.Providers;

/// <summary>
/// The one rule about a business's single sales provider (issue #759, parent #329 decision P1 as
/// revised on 18 October 2026).
///
/// <para><strong>A business has zero or one sales provider.</strong> Operators do not run two sales
/// providers at once, so <c>Business.SalesProvider</c> is the issuer of every external id stored on
/// that business's rows - which is what lets a product's or category's provider id be a column on
/// the row instead of a link table carrying its own <see cref="ExternalProvider"/>. Null means no
/// provider at all, and inventory works fully without one.</para>
///
/// <para><strong>Fixed once set.</strong> Storing credentials for a provider claims it while it is
/// null, and nothing in this application changes it afterwards: switching provider is a separate
/// design (#761), because it would have to decide what happens to every external id, sale and
/// coverage row the previous provider issued. Because nothing can switch it, every stored external
/// id belongs to the business's one provider, and a reader may resolve a provider's id by naming
/// that provider alone.</para>
///
/// <para>It holds no clock, no database and no configuration: both answers are functions of their
/// arguments, so the credential save, a test and a future provider all decide identically.</para>
/// </summary>
public static class SalesProviderPolicy
{
    /// <summary>
    /// Whether credentials for <paramref name="provider"/> may be stored for a business whose sales
    /// provider is <paramref name="salesProvider"/>.
    ///
    /// <para>Permitted while the business has no provider yet - storing the credentials is what
    /// claims it - and while the provider being stored is the one it already has. Anything else is
    /// a provider switch, which this issue deliberately cannot do.</para>
    /// </summary>
    public static bool MayStoreCredentialsFor(ExternalProvider? salesProvider, ExternalProvider provider) =>
        salesProvider is null || salesProvider == provider;

    /// <summary>
    /// Why credentials were refused, for the pair <see cref="MayStoreCredentialsFor"/> rejected.
    ///
    /// <para>It names the two providers and nothing else: no operator id, no token and no part of
    /// one, because this message reaches the API response and the log.</para>
    /// </summary>
    public static string RefusalMessage(ExternalProvider salesProvider, ExternalProvider provider) =>
        $"This business's sales provider is {salesProvider}, so {provider} credentials cannot be "
            + "stored for it. A business has one sales provider, and changing it is not supported. "
            + "Nothing was saved.";
}
