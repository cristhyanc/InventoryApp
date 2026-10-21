import { NayaxConnectionCheckName } from '../services/nayax-connection.service';

/**
 * The area each declared Nayax read proves, in an operator's language with the Nayax endpoint it
 * calls named beside it, because that endpoint is what a support request has to ask for.
 *
 * It mirrors `Inventory.Domain.Nayax.NayaxConnectionCheck` (issue #536) and is the **one**
 * vocabulary for every screen that has to say which read Nayax refused: the Owner's connection
 * checklist and support request (issue #538, `nayax-connection-checklist.component.ts`), and the
 * locked menu item and "needs a provider" page of issue #729. Two wordings for one refusal is how a
 * screen ends up asking Nayax for the wrong permission, so it lives here - beside the shared
 * availability signal every one of them reads - rather than inside one of those screens.
 *
 * **It names a read, not a Nayax permission key.** Nayax's own documentation publishes a permission
 * key and OAuth scope only for its three dashboard-widget report endpoints, and for none of the
 * reads this application depends on, so the permission an operator has to grant has no published
 * name here. Naming the read and its endpoint is the authoritative thing this application can say;
 * guessing a permission name would be worse than saying which read was refused. See
 * docs/architecture.md § Provider-locked navigation.
 */
const AREA_LABELS: Record<NayaxConnectionCheckName, string> = {
  SignIn: 'Signing in (GET devices)',
  Machines: 'Machine list (GET machines)',
  Machine: 'Machine details (GET machines/{id})',
  ProductCatalogue: 'Operator product catalogue (GET operators/{operatorId}/products)',
  ProductGroups: 'Operator product groups (GET operators/{operatorId}/productGroups)',
  MachineProducts: 'Machine product map (GET machines/{id}/machineProducts)',
  MachineLastSales: 'Machine recent sales (GET machines/{id}/lastSales)',
  MachineLastAlerts: 'Machine recent alerts (GET machines/{id}/lastAlerts)'
};

/** The published area label, or the raw name for a check a newer deployment reports. */
export function nayaxConnectionAreaLabel(check: NayaxConnectionCheckName): string {
  return AREA_LABELS[check] ?? check;
}
