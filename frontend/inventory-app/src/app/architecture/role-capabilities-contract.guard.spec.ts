import * as fs from 'fs';
import * as path from 'path';
import { ROLE_DEFINITIONS } from '../components/admin/roles/role-definitions';
import { parseBackendRoleCapabilities, parseCapabilityNamesByEnumMember } from './role-capabilities-contract.guard';

/**
 * The cross-boundary check issue #677 asks for: `role-definitions.spec.ts` pins
 * `role-definitions.ts` against itself and the published capability vocabulary, but never against
 * `Inventory.Application.Access.RoleCapabilities` - the actual backend authority. A capability
 * added, removed or moved to a different role purely on the backend disagreed with nothing.
 *
 * This reads the real `RoleCapabilities.cs` and `Capabilities.cs` source files and compares the
 * grants they declare against `ROLE_DEFINITIONS`, so the comparison's "backend" side is the
 * backend's own table, not a second hand-kept one that could drift the same way the frontend one
 * did.
 */
describe('role capabilities contract between the frontend and the backend authority (issue #677)', () => {
  describe('parsing the backend source', () => {
    const CAPABILITY_NAMES = new Map([
      ['DashboardView', 'Dashboard.View'],
      ['DashboardViewFinancials', 'Dashboard.ViewFinancials'],
      ['PickListView', 'PickList.View'],
      ['ExpensesManage', 'Expenses.Manage']
    ]);

    it('reads the published name of each capability out of the Capabilities.cs dictionary', () => {
      const fixture = `
        private static readonly FrozenDictionary<Capability, string> Names =
            new Dictionary<Capability, string>
            {
                [Capability.DashboardView] = "Dashboard.View",
                [Capability.DashboardViewFinancials] = "Dashboard.ViewFinancials",
            }.ToFrozenDictionary();
      `;

      const parsed = parseCapabilityNamesByEnumMember(fixture);

      expect(parsed.get('DashboardView')).toBe('Dashboard.View');
      expect(parsed.get('DashboardViewFinancials')).toBe('Dashboard.ViewFinancials');
      expect(parsed.size).toBe(2);
    });

    it('reads every role and the capabilities its FrozenSet grants out of the RoleCapabilities.ByRole dictionary', () => {
      const fixture = `
        private static readonly FrozenDictionary<BusinessRole, FrozenSet<Capability>> ByRole =
            new Dictionary<BusinessRole, FrozenSet<Capability>>
            {
                [BusinessRole.Operator] = FrozenSet.ToFrozenSet(
                [
                    Capability.DashboardView,
                    Capability.PickListView,
                ]),
                [BusinessRole.Manager] = FrozenSet.ToFrozenSet(
                [
                    Capability.DashboardView,
                    Capability.DashboardViewFinancials,
                    Capability.ExpensesManage,
                ]),
            }.ToFrozenDictionary();
      `;

      const parsed = parseBackendRoleCapabilities(fixture, CAPABILITY_NAMES);

      expect(parsed.get('Operator')).toEqual(new Set(['Dashboard.View', 'PickList.View']));
      expect(parsed.get('Manager')).toEqual(
        new Set(['Dashboard.View', 'Dashboard.ViewFinancials', 'Expenses.Manage'])
      );
      expect(parsed.size).toBe(2);
    });

    it('detects a capability added to a role purely on the backend', () => {
      const before = parseBackendRoleCapabilities(
        `[BusinessRole.Operator] = FrozenSet.ToFrozenSet([ Capability.DashboardView, ]),`,
        CAPABILITY_NAMES
      );
      const after = parseBackendRoleCapabilities(
        `[BusinessRole.Operator] = FrozenSet.ToFrozenSet([ Capability.DashboardView, Capability.ExpensesManage, ]),`,
        CAPABILITY_NAMES
      );

      expect(before.get('Operator')).toEqual(new Set(['Dashboard.View']));
      expect(after.get('Operator')).toEqual(new Set(['Dashboard.View', 'Expenses.Manage']));
      expect(after.get('Operator')).not.toEqual(before.get('Operator'));
    });

    it('detects a capability reassigned from one role to another', () => {
      const parsed = parseBackendRoleCapabilities(
        `
        [BusinessRole.Operator] = FrozenSet.ToFrozenSet([ Capability.DashboardView, ]),
        [BusinessRole.Manager] = FrozenSet.ToFrozenSet([ Capability.DashboardView, Capability.ExpensesManage, ]),
        `,
        CAPABILITY_NAMES
      );

      expect(parsed.get('Operator')).not.toContain('Expenses.Manage');
      expect(parsed.get('Manager')).toContain('Expenses.Manage');
    });

    it('fails loudly rather than silently passing when it finds no role block at all', () => {
      expect(() => parseBackendRoleCapabilities('// RoleCapabilities rewritten with no ByRole table', CAPABILITY_NAMES)).toThrow();
    });

    it('fails loudly when a role grants a capability Capabilities has no published name for', () => {
      expect(() =>
        parseBackendRoleCapabilities(
          `[BusinessRole.Operator] = FrozenSet.ToFrozenSet([ Capability.SomeNewCapability, ]),`,
          CAPABILITY_NAMES
        )
      ).toThrow();
    });
  });

  describe('the actual backend authority, read from Inventory.Application.Access', () => {
    const repoRoot = path.resolve(__dirname, '../../../../..');
    const accessDir = path.resolve(repoRoot, 'backend/Inventory.Application/Access');
    const capabilitiesSource = fs.readFileSync(path.resolve(accessDir, 'Capabilities.cs'), 'utf8');
    const roleCapabilitiesSource = fs.readFileSync(path.resolve(accessDir, 'RoleCapabilities.cs'), 'utf8');

    const capabilityNamesByEnumMember = parseCapabilityNamesByEnumMember(capabilitiesSource);
    const backendCapabilitiesByRole = parseBackendRoleCapabilities(roleCapabilitiesSource, capabilityNamesByEnumMember);

    it('found a published name for every capability RoleCapabilities.cs grants', () => {
      expect(capabilityNamesByEnumMember.size).toBeGreaterThan(0);
    });

    it('declares exactly the same roles the frontend describes: Operator, Manager and Owner', () => {
      expect(new Set(backendCapabilitiesByRole.keys())).toEqual(new Set(ROLE_DEFINITIONS.map((definition) => definition.role)));
    });

    it.each(ROLE_DEFINITIONS.map((definition) => definition.role))(
      "grants %s exactly the capabilities role-definitions.ts describes, and nothing else",
      (role) => {
        const frontendDefinition = ROLE_DEFINITIONS.find((definition) => definition.role === role)!;
        const frontendCapabilities = new Set(frontendDefinition.capabilities);
        const backendCapabilities = backendCapabilitiesByRole.get(role);

        expect(backendCapabilities).toBeDefined();
        expect(backendCapabilities).toEqual(frontendCapabilities);
      }
    );
  });
});
