import { CAPABILITIES, Capability } from '../../../access/capabilities';
import { CAPABILITY_DESCRIPTIONS, ROLE_DEFINITIONS } from './role-definitions';

/**
 * The drift test issue #530 asks for: this page is rendered from one frontend definition, and
 * these assertions fail the moment that definition and the published capability vocabulary
 * (`access/capabilities.ts`, itself mirrored from `Inventory.Application.Access.Capabilities`)
 * disagree - a capability added to the vocabulary with no description or no role, or a
 * description left behind for one that was removed.
 */
describe('role definitions kept in sync with the capability vocabulary (issue #530)', () => {
  it('describes every published capability, and nothing else', () => {
    const described = Object.keys(CAPABILITY_DESCRIPTIONS);

    expect(new Set(described)).toEqual(new Set(CAPABILITIES));
    expect(described).toHaveLength(CAPABILITIES.length);
  });

  it('gives every capability a non-empty description', () => {
    for (const capability of CAPABILITIES) {
      expect(CAPABILITY_DESCRIPTIONS[capability].trim().length).toBeGreaterThan(0);
    }
  });

  function capabilitiesOf(role: 'Owner' | 'Manager' | 'Operator'): readonly Capability[] {
    const definition = ROLE_DEFINITIONS.find((candidate) => candidate.role === role);
    expect(definition).toBeDefined();
    return definition!.capabilities;
  }

  it('grants every published capability to at least one role', () => {
    const union = new Set(ROLE_DEFINITIONS.flatMap((definition) => definition.capabilities));

    expect(union).toEqual(new Set(CAPABILITIES));
  });

  /**
   * Written out literally from the agreed table of #328 rather than derived from `RoleCapabilities`
   * itself (the same convention `navigation.spec.ts` uses), so a role that quietly gains or loses
   * a capability on the frontend disagrees with this test instead of agreeing with itself.
   */
  it('keeps the hierarchy Operator ⊂ Manager ⊂ Owner, with Owner holding every capability', () => {
    const operator = capabilitiesOf('Operator');
    const manager = capabilitiesOf('Manager');
    const owner = capabilitiesOf('Owner');

    expect(operator).toEqual(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);
    expect(manager).toEqual([
      'Dashboard.View',
      'Dashboard.ViewFinancials',
      'PickList.View',
      'Inventory.Operate',
      'Purchasing.Operate',
      'Suppliers.ManageGstDefaults',
      'Expenses.Manage',
      'Reports.View',
      'Imports.Run',
      'SiteCommissions.Manage'
    ]);
    expect(owner).toEqual([...CAPABILITIES]);

    for (const capability of operator) {
      expect(manager).toContain(capability);
    }
    for (const capability of manager) {
      expect(owner).toContain(capability);
    }
  });

  it('declares no capability outside the published vocabulary', () => {
    for (const definition of ROLE_DEFINITIONS) {
      for (const capability of definition.capabilities) {
        expect(CAPABILITIES).toContain(capability);
      }
    }
  });
});
