import { nayaxConnectionAreaLabel } from './nayax-read-areas';
import { ProviderConnection, ProviderFeatureAvailability, providerFeatureOffered } from './provider-availability.service';
import { PROVIDER_FEATURE_CHECKS, ProviderFeature } from './provider-feature-permissions';

/**
 * What the shared provider-availability signal (issue #728) means for one **destination** - a menu
 * entry, its route guard and the "needs a provider" page - as child E2 of issue #329 (decision P6)
 * asks for.
 *
 * This file holds nothing but pure functions, deliberately: the menu, the guard on the route and the
 * page the guard redirects to must reach the same verdict about one destination from the same
 * answer. A menu that locked an item whose page then opened, or a page that refused an item the menu
 * offered, is the one failure this separation prevents.
 *
 * **It is a translation of issue #728's answer, never a second opinion.** `providerLockOf` is
 * `providerFeatureOffered` with the refused case split in two, because those two refusals need
 * opposite things from an operator: a connection, or a permission on a connection that already
 * works. In particular `unknown` fails **open** here for exactly the reason it does there - a status
 * probe that failed, and a usable connection no test or refusal has said anything about, must not
 * lock a page whose own request would have answered it.
 *
 * See docs/architecture.md § Provider-locked navigation.
 */

/** The address of the "needs a provider" page, declared once for the guard and the router. */
export const PROVIDER_LOCKED_PATH = 'needs-provider';

/**
 * The query parameter the guard uses to hand the locked page the URL it refused, so the page can
 * name the destination the operator actually asked for. It carries no decision: the page resolves
 * the destination from the declared navigation and re-reads the availability answer itself.
 */
export const PROVIDER_LOCKED_FROM_PARAM = 'from';

/** What one destination's provider dependency does to it right now. */
export type ProviderLock =
  /** The feature is available, or nothing has refused it: the destination behaves normally. */
  | 'open'
  /** The connection cannot serve the feature at all: the business has to connect Nayax. */
  | 'connect'
  /** The connection works and Nayax has refused this feature's read for lack of permission. */
  | 'permission';

/**
 * What one feature's availability means for a destination that needs it. The two locked states are
 * answers rather than labels on each other: telling an operator with working credentials that Nayax
 * is not connected sends them to re-enter a token that was never the problem.
 */
export function providerLockOf(availability: ProviderFeatureAvailability): ProviderLock {
  if (providerFeatureOffered(availability)) {
    return 'open';
  }
  return availability === 'permissionNotGranted' ? 'permission' : 'connect';
}

/**
 * Whether the provider repair and admin tools are offered at all (issue #729): the Nayax sales and
 * settlement imports and the Nayax sale timestamp repair are hidden until the connection is usable,
 * because without one there is nothing of the provider's to import or repair - a locked entry would
 * offer an operator a tool for data that does not exist.
 *
 * `unknown` keeps them, for the same fail-open reason a destination does not lock on an unread
 * status: a failed status probe must not take an Owner's repair tools away.
 */
export function providerToolsOffered(connection: ProviderConnection): boolean {
  return connection === 'usable' || connection === 'unknown';
}

/**
 * The declared Nayax reads one feature depends on, each named with its endpoint.
 *
 * This is the closest thing to "the missing permission" that exists: `PROVIDER_FEATURE_CHECKS`
 * (issue #728) maps a feature to the reads this application declares in
 * `Inventory.Domain.Nayax.NayaxConnectionCheck`, and `nayax-read-areas.ts` words them once. Nayax's
 * own documentation publishes a permission key and OAuth scope for its dashboard-widget report
 * endpoints and for none of these reads, and the `409 nayax_permission_not_granted` refusal
 * deliberately carries no operation name, so no permission name is available to print and none is
 * invented here.
 */
export function providerFeatureReadLabels(feature: ProviderFeature): readonly string[] {
  return PROVIDER_FEATURE_CHECKS[feature].map(nayaxConnectionAreaLabel);
}

/**
 * The one short line a locked menu entry carries, naming the destination it is about: availability
 * is per feature, so "Machines is waiting for a Nayax permission" may sit beside an unlocked entry
 * whose own read Nayax did grant.
 */
export function providerLockSummary(lock: ProviderLock, destination: string): string {
  switch (lock) {
    case 'connect':
      return `${destination} is unavailable because Nayax is not connected.`;
    case 'permission':
      return `${destination} is waiting for a Nayax permission.`;
    default:
      return '';
  }
}

/**
 * The sentence the "needs a provider" page shows: the summary, plus what the operator can do about
 * it. A permission lock names the refused read, because that read and its endpoint are what a
 * support request to Nayax has to ask for; a connection lock names none, because none of them was
 * refused - nothing was asked.
 */
export function providerLockSentence(lock: ProviderLock, destination: string, feature: ProviderFeature): string {
  const summary = providerLockSummary(lock, destination);
  if (lock === 'connect') {
    return `${summary} It reads live data from Nayax, so it stays locked until this business has a Nayax connection this application can use.`;
  }
  if (lock !== 'permission') {
    return summary;
  }

  const reads = providerFeatureReadLabels(feature);
  const refused =
    reads.length === 0
      ? 'Nayax has refused the read behind it.'
      : `Nayax has not granted this application permission for: ${reads.join('; ')}.`;
  return `${summary} The connection itself is working. ${refused}`;
}
