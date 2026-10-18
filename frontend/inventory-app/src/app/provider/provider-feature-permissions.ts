import { NayaxConnectionCheckLine, NayaxConnectionCheckName } from '../services/nayax-connection.service';

/**
 * The provider features this frontend asks about, which declared Nayax read each one depends on,
 * and what is actually *known* about that read's permission (issue #728, child E1 of issue #329).
 *
 * **Why this is its own vocabulary.** A connection status answers one question - may this
 * application use the business's stored credentials at all - and `PendingPermissions` is a usable
 * status precisely because the business's Nayax account may have granted some reads and not others
 * (`Inventory.Domain.Nayax.NayaxConnectionStatusGate`). So a usable connection is **not** evidence
 * that any particular feature's read is granted, and treating it as such is how a page that could
 * work gets hidden beside one that could not. Each feature therefore carries its own
 * {@link ProviderFeaturePermission}, and `unknown` is one of its answers rather than a synonym for
 * granted.
 *
 * **A feature is a kind of Nayax read, not a page.** The names below are the reads this application
 * declares in `Inventory.Domain.Nayax.NayaxConnectionCheck` - the one place it says which Nayax
 * endpoints it depends on - so a new screen asks an existing question and the evidence a connection
 * test already produces answers it. {@link PROVIDER_FEATURE_CHECKS} is that mapping, and it is the
 * feature-to-permission mapping issue #729 (E2) reads for a locked menu item.
 *
 * **What this file cannot do, and does not pretend to.** `GET /api/settings/nayax-connection/status`
 * publishes a status and `machineChecksPending`, and nothing per read; the `409
 * nayax_permission_not_granted` refusal publishes no operation name either (the refused operation is
 * logged on the server and deliberately kept out of the response). So evidence exists only where the
 * application itself produced it: the check lines of an Owner's connection test or re-test, and a
 * refusal retained against the feature whose request earned it. Everything else is `unknown`, and
 * naming the Nayax permission an operator has to grant needs a backend contract this application
 * does not have yet - see docs/architecture.md § Shared provider availability and error-versus-empty
 * states.
 */

/** One kind of dependency on the provider. Deliberately a read, not a screen. */
export type ProviderFeature =
  /** The operator's machine list, and the site list derived from it: `GET /api/machines`, `GET /api/sites`. */
  | 'machineFleet'
  /** A machine's latest sales read from Nayax. */
  | 'machineSales'
  /** A machine's own product/stock state read from Nayax. */
  | 'machineStock'
  /** The operator's product catalogue and product groups. */
  | 'productCatalogue'
  /**
   * Anything that only needs Nayax to be *part of* this business and makes no live read at all: the
   * "Nayax Sync Restock" source of an already-recorded stock movement, the "Nayax monthly fee"
   * expense category. No permission can be granted or refused for it, so its permission stays
   * `unknown` and only the connection state answers it.
   */
  | 'providerRecords';

/** Every feature, in the order this file declares them. */
export const PROVIDER_FEATURES: readonly ProviderFeature[] = [
  'machineFleet',
  'machineSales',
  'machineStock',
  'productCatalogue',
  'providerRecords'
];

/**
 * What is known about one feature's Nayax permission. `unknown` is the ordinary state of a fresh
 * session: no contract reports a per-read permission, so nothing has been stated yet.
 */
export type ProviderFeaturePermission = 'granted' | 'denied' | 'unknown';

/** What is known about every feature's permission. */
export type ProviderPermissionEvidence = Readonly<Record<ProviderFeature, ProviderFeaturePermission>>;

/**
 * The declared connection checks whose result is evidence about each feature. A check no feature
 * claims - `SignIn`, which is about the credentials rather than about a read, and `Machine` and
 * `MachineLastAlerts`, which no screen depends on yet - is evidence about nothing here, and is
 * never read as evidence about a feature that merely looks similar.
 */
export const PROVIDER_FEATURE_CHECKS: Readonly<Record<ProviderFeature, readonly NayaxConnectionCheckName[]>> = {
  machineFleet: ['Machines'],
  machineSales: ['MachineLastSales'],
  machineStock: ['MachineProducts'],
  productCatalogue: ['ProductCatalogue', 'ProductGroups'],
  providerRecords: []
};

/** Nothing stated about any feature: the state of a fresh session, and of a reset one. */
export const UNKNOWN_PROVIDER_PERMISSIONS: ProviderPermissionEvidence = Object.freeze({
  machineFleet: 'unknown',
  machineSales: 'unknown',
  machineStock: 'unknown',
  productCatalogue: 'unknown',
  providerRecords: 'unknown'
} as const);

/**
 * Whether the feature is answered by a live Nayax read, and so needs credentials the status gate
 * will use. Only `providerRecords` is not: what it offers is already recorded locally.
 */
export function providerFeatureNeedsLiveRead(feature: ProviderFeature): boolean {
  return feature !== 'providerRecords';
}

/**
 * What one completed test or re-test proves about each feature's permission (issues #536, #538).
 *
 * A check refused with `PermissionNotGranted` is the only thing read as `denied`, and it outweighs a
 * sibling check that passed: a feature that needs two reads needs both. A check that passed is
 * `granted`. Everything else - `NotChecked` (the machine-scoped checks never ran, because the
 * business has no machine yet), an invalid token, a Nayax outage, an answer nobody interpreted -
 * leaves the feature `unknown`, because a read nobody completed says nothing about a permission.
 */
export function providerPermissionsOfChecks(
  checks: readonly NayaxConnectionCheckLine[]
): ProviderPermissionEvidence {
  const evidence: Record<ProviderFeature, ProviderFeaturePermission> = { ...UNKNOWN_PROVIDER_PERMISSIONS };

  for (const feature of PROVIDER_FEATURES) {
    const names = PROVIDER_FEATURE_CHECKS[feature];
    const lines = checks.filter((line) => names.includes(line.check));
    if (lines.some((line) => line.status === 'Failed' && line.failure === 'PermissionNotGranted')) {
      evidence[feature] = 'denied';
    } else if (lines.some((line) => line.status === 'Passed')) {
      evidence[feature] = 'granted';
    }
  }

  return evidence;
}

/** The same evidence with one feature's permission replaced, leaving every other feature alone. */
export function withProviderPermission(
  evidence: ProviderPermissionEvidence,
  feature: ProviderFeature,
  permission: ProviderFeaturePermission
): ProviderPermissionEvidence {
  return { ...evidence, [feature]: permission };
}
