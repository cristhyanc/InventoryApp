/**
 * The capability vocabulary `GET /api/me/access` reports (issue #521), as the published
 * `Area.Action` names and in the order the API returns them.
 *
 * These are a backend contract, mirrored here rather than derived: the names are what
 * `Inventory.Application.Access.Capabilities` writes out, and the frontend only ever asks whether
 * a name is in the set the API sent. There is deliberately no role in this file and no mapping
 * from a role to a capability - `Inventory.Application.Access.RoleCapabilities` is the one table
 * that owns that, and a second copy in the browser could only diverge from it.
 *
 * Nothing here is a boundary. Every capability the frontend reads decides what is *rendered*; the
 * endpoint behind the page checks the same capability itself and refuses the request regardless
 * (see docs/architecture.md § Capability authorization).
 */
export const CAPABILITIES = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Suppliers.ManageGstDefaults',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Data.Repair',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
] as const;

/** One capability name from the declared vocabulary. There is no wildcard and no "other". */
export type Capability = (typeof CAPABILITIES)[number];

/** The capabilities a member holds. Membership is the only question ever asked of it. */
export type CapabilitySet = ReadonlySet<Capability>;

const DECLARED: ReadonlySet<string> = new Set<string>(CAPABILITIES);

/** Whether `name` is a capability this build declares. */
export function isCapability(name: string): name is Capability {
  return DECLARED.has(name);
}

/**
 * The capability set a `GET /api/me/access` response describes.
 *
 * A name this build does not declare is dropped rather than kept as an opaque string: a newer
 * deployment may report a capability this bundle knows nothing about, and nothing here can decide
 * what to render for it. Dropping it hides whatever that capability would have shown, which is the
 * fail-closed direction, and the API still answers the request on its own terms.
 */
export function capabilitySet(names: readonly string[]): CapabilitySet {
  return new Set<Capability>(names.filter(isCapability));
}
