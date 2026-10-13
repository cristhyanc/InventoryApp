import { CAPABILITIES, Capability } from '../../../access/capabilities';

/**
 * The content for the Admin → Roles page (issue #530, one slice of #503 under #328): a read-only
 * explanation of what Owner, Manager and Operator can each access.
 *
 * `docs/architecture.md` § Member access says there is deliberately no frontend role source for a
 * navigation or authorization decision, because a second copy of
 * `Inventory.Application.Access.RoleCapabilities` could only diverge from it. This page is not one
 * of those decisions - it hides nothing and guards nothing, and the capability guard on its own
 * route is the same presentation-only guard every other page carries. But explaining the *whole*
 * matrix needs the matrix, and no endpoint reports it (`GET /api/me/access` reports only the
 * caller's own capabilities), so this file is a deliberate, bounded exception: one written-out
 * table, reviewed the same way the backend's own table is, kept from drifting by
 * `role-definitions.spec.ts` rather than by a shared source.
 *
 * Each role's capability list is written out in full, in the published vocabulary's order, rather
 * than derived - the same convention `Inventory.Application.Access.RoleCapabilities` and this
 * frontend's own `navigation.spec.ts` already use for the same reason: a table that referenced
 * itself would agree with itself no matter what it said.
 */
export interface RoleAccessDefinition {
  readonly role: 'Owner' | 'Manager' | 'Operator';
  readonly summary: string;
  readonly capabilities: readonly Capability[];
}

/** A human-readable sentence for every published capability, independent of which role holds it. */
export const CAPABILITY_DESCRIPTIONS: Readonly<Record<Capability, string>> = {
  'Dashboard.View': 'View the dashboard.',
  'Dashboard.ViewFinancials': "See the dashboard's financial figures: sales this week and inventory value at cost.",
  'PickList.View': 'View the pick list.',
  'Inventory.Operate':
    'Manage products, categories, take inventory, stock history, machines (including Sync Restock), and sites.',
  'Purchasing.Operate': 'Record purchases, manage supplier orders, and view suppliers.',
  'Suppliers.ManageGstDefaults': "Change a supplier's GST defaults.",
  'Expenses.Manage': 'Record and manage operating expenses and their attachments.',
  'Reports.View': 'View and export every report: bookkeeping, daily, transactions, reconciliation, machines, products and GST.',
  'Imports.Run': 'Run Admin → Imports: Nayax sales, product catalogue and pending reimbursement imports.',
  'SiteCommissions.Manage': 'Manage site commission agreements and view the site commission report.',
  'Integration.Manage': 'Configure the Nayax integration: the connection wizard and the processing-fee rates.',
  'Data.Repair':
    'Run the data-repair maintenance tools: Costing Repair, AVCO Transition, Historical GST Classification, Nayax Sale Timestamp Repair and Nayax Historical Cost Recovery.',
  'Members.Manage': 'Manage members and invite links: change roles, revoke, reinstate, and create invite links.',
  'Roles.View': 'View this page: what each role can access.',
  'Business.Manage': "Manage the business's own record."
} as const;

const OPERATOR_CAPABILITIES: readonly Capability[] = [
  'Dashboard.View',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate'
];

const MANAGER_CAPABILITIES: readonly Capability[] = [
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
];

const OWNER_CAPABILITIES: readonly Capability[] = [...CAPABILITIES];

/** What Owner, Manager and Operator can each access, in that order (the order README.md uses). */
export const ROLE_DEFINITIONS: readonly RoleAccessDefinition[] = [
  {
    role: 'Owner',
    summary: 'Everything, including the Nayax integration, data repairs, members, roles and the business record itself.',
    capabilities: OWNER_CAPABILITIES
  },
  {
    role: 'Manager',
    summary:
      'Everything operational, plus the financial figures: dashboard financials, reports, operating expenses, imports and site commissions.',
    capabilities: MANAGER_CAPABILITIES
  },
  {
    role: 'Operator',
    summary:
      'Day-to-day work only: the dashboard without financial figures, the pick list, inventory and purchasing.',
    capabilities: OPERATOR_CAPABILITIES
  }
];
