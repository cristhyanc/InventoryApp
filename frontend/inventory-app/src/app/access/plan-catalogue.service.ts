import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ConfigService } from '../services/config.service';

/**
 * One plan's price and inclusions, as `GET /api/me/plan-catalogue` reports it (issue #970), from
 * `Inventory.Domain.Subscriptions.PlanCatalogue`.
 */
export interface PlanCatalogueEntry {
  /** The plan's published name - `Free`, `Starter` or `Pro`. */
  readonly plan: string;

  /** The advertised monthly price, in minor units of {@link currencyCode}. */
  readonly monthlyPriceMinorUnits: number;

  /** The ISO 4217 code the price is in. */
  readonly currencyCode: string;

  /** The plan's combined purchase/expense attachment storage allowance, in bytes. */
  readonly attachmentStorageLimitBytes: number;

  /** How many connected machines the price includes. */
  readonly includedConnectedMachines: number;

  /**
   * What each connected machine beyond {@link includedConnectedMachines} costs per month, or
   * `null` when the plan offers no connected machines to add to.
   */
  readonly additionalConnectedMachinePriceMinorUnits: number | null;
}

interface MePlanCatalogueResponse {
  readonly plans: readonly PlanCatalogueEntry[];
}

/**
 * Reads the Free/Starter/Pro catalogue from `GET /api/me/plan-catalogue` (issue #970): every
 * plan's price, storage allowance and connected-machine terms, from the one server-side definition
 * (`Inventory.Domain.Subscriptions.PlanCatalogue`) rather than a second copy kept in Angular.
 *
 * It is visible to every member and identical for every caller - it names no business and nothing
 * about the business changes it - so there is nothing here to cache per signed-in identity or to
 * re-read on a support session or role preview change, unlike `AccessService`.
 */
@Injectable({ providedIn: 'root' })
export class PlanCatalogueService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** Every plan, in the catalogue's own order. */
  get(): Observable<readonly PlanCatalogueEntry[]> {
    return this.http
      .get<MePlanCatalogueResponse>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/me/plan-catalogue`)
      .pipe(map((response) => response.plans));
  }
}
