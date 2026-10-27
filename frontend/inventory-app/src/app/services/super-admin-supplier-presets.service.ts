import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The states and territories a preset may serve, as the server's own codes and in the server's own
 * order (backend `Inventory.Domain.Suppliers.SupplierPresetMasterPolicy.ServedStateCodes`, issue
 * #931).
 *
 * The codes themselves are the contract - not numbers standing for them - so there is no translation
 * table here to drift from the server's. The order matters: it is the order the server stores and
 * reports a market in, and the order the form offers the choices in.
 */
export const SUPPLIER_SERVED_STATES = ['NSW', 'VIC', 'QLD', 'SA', 'WA', 'TAS', 'ACT', 'NT'] as const;

/** One of {@link SUPPLIER_SERVED_STATES}. */
export type SupplierServedState = (typeof SUPPLIER_SERVED_STATES)[number];

/**
 * How a supplier's prices are collected, as the API serializes it (backend
 * `Inventory.Domain.Suppliers.SupplierPriceConnector`, issue #931). The numbers are the contract, so
 * they must stay in step with that enum.
 *
 * `null` - no connector at all - is a real state and not a missing value: it is the state a supplier
 * somebody price-checks by hand is in. There is deliberately no `None = 0` member, because the server
 * refuses `0` as an undeclared route rather than reading it as "none".
 *
 * `BigW` is one route even though the collector reaches it by a name search with a direct SKU lookup
 * behind it: a preset says which route to take, never which calls it makes.
 */
export enum SupplierPriceConnector {
  Varnmill = 1,
  Kmart = 2,
  BigW = 3,
  JbMetro = 4
}

/**
 * One platform supplier preset as `GET /api/super-admin/supplier-presets` reports it (issues #848
 * and #931). These eleven fields are the whole contract: the stored normalised name the server
 * compares by is deliberately not published, because whether two names collide is the database's
 * answer and not a decision any client makes.
 */
export interface SuperAdminSupplierPreset {
  readonly id: number;
  readonly name: string;
  readonly url: string | null;
  readonly isEnabled: boolean;
  /** Whether it serves the whole country. Mutually exclusive with {@link servedStates}. */
  readonly servesNationwide: boolean;
  /**
   * The states and territories it serves, in the server's order. Empty when {@link servesNationwide}
   * is `true`, and also empty for a preset nobody has recorded a market for yet - which the page
   * reports as not set rather than as nationwide.
   */
  readonly servedStates: readonly string[];
  /** Whether the prices it publishes include GST. Never inferred from anything else. */
  readonly pricesIncludeGst: boolean;
  readonly priceConnector: SupplierPriceConnector | null;
  /** Which retailer the connector is for, or `null` where the route takes no key. */
  readonly priceConnectorKey: string | null;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/** What a create submits. A new preset is created enabled, so there is no `isEnabled`. */
export interface CreateSupplierPresetRequest {
  readonly name: string;
  readonly url: string | null;
  readonly servesNationwide: boolean;
  readonly servedStates: readonly string[];
  readonly pricesIncludeGst: boolean;
  readonly priceConnector: SupplierPriceConnector | null;
  readonly priceConnectorKey: string | null;
}

/** What an edit submits: the whole resource, so enabling and disabling is this call. */
export interface UpdateSupplierPresetRequest {
  readonly name: string;
  readonly url: string | null;
  readonly isEnabled: boolean;
  readonly servesNationwide: boolean;
  readonly servedStates: readonly string[];
  readonly pricesIncludeGst: boolean;
  readonly priceConnector: SupplierPriceConnector | null;
  readonly priceConnectorKey: string | null;
}

/**
 * What a connector is called on screen. Written out rather than derived from the enum's member names
 * because "BIG W" and "JB Metro" are how those retailers spell themselves, and a label is for a
 * person to read.
 */
export const SUPPLIER_PRICE_CONNECTOR_LABELS: Readonly<Record<SupplierPriceConnector, string>> = {
  [SupplierPriceConnector.Varnmill]: 'Varnmill',
  [SupplierPriceConnector.Kmart]: 'Kmart',
  [SupplierPriceConnector.BigW]: 'BIG W',
  [SupplierPriceConnector.JbMetro]: 'JB Metro'
};

/**
 * The frontend client for `/api/super-admin/supplier-presets` (issue #848), consumed by the Super
 * Admin Supplier presets page. It is a thin wrapper over the three endpoints: no name, URL or
 * duplicate rule is decided here, only what the server already returned.
 *
 * There is deliberately no `delete`: the API has none, and a preset that should stop being suggested
 * is disabled through {@link update}.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminSupplierPresetsService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** Every preset, enabled and disabled alike, ordered by name. No filter parameter exists. */
  list(): Observable<SuperAdminSupplierPreset[]> {
    return this.http.get<SuperAdminSupplierPreset[]>(this.baseUrl);
  }

  create(request: CreateSupplierPresetRequest): Observable<SuperAdminSupplierPreset> {
    return this.http.post<SuperAdminSupplierPreset>(this.baseUrl, request);
  }

  update(id: number, request: UpdateSupplierPresetRequest): Observable<SuperAdminSupplierPreset> {
    return this.http.put<SuperAdminSupplierPreset>(`${this.baseUrl}/${id}`, request);
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/supplier-presets`;
  }
}
