import { NayaxConnectionCheckLine, NayaxConnectionCheckName } from '../services/nayax-connection.service';
import {
  PROVIDER_FEATURES,
  PROVIDER_FEATURE_CHECKS,
  UNKNOWN_PROVIDER_PERMISSIONS,
  providerFeatureNeedsLiveRead,
  providerPermissionsOfChecks,
  withProviderPermission
} from './provider-feature-permissions';

function line(
  check: NayaxConnectionCheckName,
  status: NayaxConnectionCheckLine['status'],
  failure: NayaxConnectionCheckLine['failure'] = null
): NayaxConnectionCheckLine {
  return { check, status, failure };
}

/**
 * The feature-to-permission mapping and the evidence rule behind it (issue #728, the vocabulary
 * issue #729 (E2) reads). What matters here is what is *not* concluded: a check nobody completed, a
 * check that failed for a reason other than a permission, and a check no feature claims all leave a
 * feature `unknown`, which is an answer rather than a guess.
 */
describe('providerPermissionsOfChecks (issue #728)', () => {
  it('states nothing at all when no check ran', () => {
    expect(providerPermissionsOfChecks([])).toEqual(UNKNOWN_PROVIDER_PERMISSIONS);
  });

  it('reads a passed check as the permission for that feature being granted', () => {
    const permissions = providerPermissionsOfChecks([line('Machines', 'Passed')]);

    expect(permissions.machineFleet).toBe('granted');
    expect(permissions.machineSales).toBe('unknown');
  });

  it('tells a granted machine read apart from a refused sales read in one run', () => {
    const permissions = providerPermissionsOfChecks([
      line('SignIn', 'Passed'),
      line('Machines', 'Passed'),
      line('MachineLastSales', 'Failed', 'PermissionNotGranted')
    ]);

    expect(permissions.machineFleet).toBe('granted');
    expect(permissions.machineSales).toBe('denied');
  });

  it('lets a refused read outweigh a passed sibling: a feature that needs two reads needs both', () => {
    const permissions = providerPermissionsOfChecks([
      line('ProductCatalogue', 'Passed'),
      line('ProductGroups', 'Failed', 'PermissionNotGranted')
    ]);

    expect(permissions.productCatalogue).toBe('denied');
  });

  it.each<[NayaxConnectionCheckLine['status'], NayaxConnectionCheckLine['failure']]>([
    ['NotChecked', null],
    ['Failed', 'NayaxUnavailable'],
    ['Failed', 'InvalidOrExpiredToken'],
    ['Failed', 'UnexpectedResponse'],
    ['Failed', null]
  ])('leaves the feature unknown for a %s / %s check, which proves nothing about a permission', (status, failure) => {
    expect(providerPermissionsOfChecks([line('MachineLastSales', status, failure)]).machineSales).toBe('unknown');
  });

  it('concludes nothing from a check no feature claims', () => {
    const permissions = providerPermissionsOfChecks([
      line('SignIn', 'Passed'),
      line('Machine', 'Passed'),
      line('MachineLastAlerts', 'Failed', 'PermissionNotGranted')
    ]);

    expect(permissions).toEqual(UNKNOWN_PROVIDER_PERMISSIONS);
  });

  it('never states a permission for a feature that makes no live read', () => {
    expect(PROVIDER_FEATURE_CHECKS.providerRecords).toEqual([]);
    expect(providerFeatureNeedsLiveRead('providerRecords')).toBe(false);
    expect(PROVIDER_FEATURES.filter((feature) => !providerFeatureNeedsLiveRead(feature))).toEqual([
      'providerRecords'
    ]);
  });

  it('maps every feature, so a feature can never be silently unmapped', () => {
    for (const feature of PROVIDER_FEATURES) {
      expect(PROVIDER_FEATURE_CHECKS[feature]).toBeDefined();
      expect(UNKNOWN_PROVIDER_PERMISSIONS[feature]).toBe('unknown');
    }
  });
});

describe('withProviderPermission (issue #728)', () => {
  it('replaces one feature and leaves every other one exactly as it was', () => {
    const denied = withProviderPermission(UNKNOWN_PROVIDER_PERMISSIONS, 'machineSales', 'denied');

    expect(denied.machineSales).toBe('denied');
    expect(denied.machineFleet).toBe('unknown');
    expect(UNKNOWN_PROVIDER_PERMISSIONS.machineSales).toBe('unknown');
  });
});
