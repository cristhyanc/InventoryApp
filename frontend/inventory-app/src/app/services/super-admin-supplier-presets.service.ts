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
 * How often a supplier's prices are searched, as the API serializes it (backend
 * `Inventory.Domain.PriceCatalogue.SupplierSearchFrequency`, issue #1108). The numbers are the
 * contract, so they must stay in step with that enum - `supplier-search-schedule.spec.ts` pins them
 * against the shared test-case file the backend tests read.
 *
 * There is deliberately no `None = 0`: every supplier has a schedule, and the switch that decides
 * whether anything is searched at all is {@link SuperAdminSupplierPreset.searchPrices}.
 */
export enum SupplierSearchFrequency {
  Daily = 1,
  Weekly = 2,
  Fortnightly = 3
}

/**
 * The weekdays a search schedule may name, as the server's own codes and in the server's own
 * Monday-to-Sunday order (backend `SupplierSearchSchedulePolicy.Weekdays`, issue #1108).
 *
 * The order is the contract as well as the display order: the weeks of this schedule run Monday to
 * Sunday, and one set of days has exactly one spelling on the wire, so the page offers, shows and
 * sends them in this order and no other.
 */
export const SUPPLIER_SEARCH_WEEKDAYS = ['MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT', 'SUN'] as const;

/** One of {@link SUPPLIER_SEARCH_WEEKDAYS}. */
export type SupplierSearchWeekday = (typeof SUPPLIER_SEARCH_WEEKDAYS)[number];

/**
 * One platform supplier preset as `GET /api/super-admin/supplier-presets` reports it (issues #848,
 * #931, #1077 and #1108). These seventeen fields are the whole contract: the stored normalised name
 * the server compares by is deliberately not published, because whether two names collide is the
 * database's answer and not a decision any client makes.
 */
export interface SuperAdminSupplierPreset {
  readonly id: number;
  readonly name: string;
  readonly url: string | null;
  /**
   * Whether the preset is still suggested to businesses. Since issue #1077 it says only that:
   * whether the collector searches this supplier is {@link searchPrices}.
   */
  readonly isEnabled: boolean;
  /**
   * Whether the price collector searches this supplier (issue #1077). Independent of
   * {@link isEnabled} - a supplier may be suggested and unsearched, or searched and no longer
   * suggested - and it decides nothing at all for a preset whose {@link priceConnector} is `null`,
   * because there is no route to take.
   */
  readonly searchPrices: boolean;
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
  /** How often the collector searches this supplier (issue #1108). */
  readonly searchFrequency: SupplierSearchFrequency;
  /**
   * The weekdays it is searched on, as the server's codes in its own Monday-to-Sunday order. Empty
   * for a daily schedule, where every day is one.
   */
  readonly searchDaysOfWeek: readonly string[];
  /**
   * The date whose week, Monday to Sunday, is an *on* week, as `YYYY-MM-DD`, for a fortnightly
   * schedule and `null` for every other (issue #1108).
   *
   * **It is the characters the server sent and nothing else.** It is deliberately typed as a string
   * rather than parsed into a `Date`: a date names a day of the collection calendar rather than a
   * moment, and reading it through the browser's zone would move the on week by a day for a Super
   * Admin outside Sydney. Format it with `formatSearchDateOnly`, never with `new Date(...)`.
   */
  readonly fortnightStartDate: string | null;
  /**
   * When that schedule last became effective (issue #1108). Reported so the page can show it; the
   * server decides it, and no client writes it.
   */
  readonly scheduleEffectiveFromUtc: string;
  /**
   * How old an observed price of this supplier may be and still be compared (issues #925 and
   * #1108), in hours. **Reported, never submitted**: the server derives it from the schedule, so a
   * value sent in a request body is ignored and the two cannot be made to disagree. The page shows
   * this value, and the dialog previews what a schedule being edited would derive with
   * `searchFreshnessHours`.
   */
  readonly priceCheckFreshnessHours: number;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/**
 * The three schedule fields a create or an edit submits (issue #1108), which are the same three on
 * both calls.
 *
 * The server treats all three as optional and reads "none of them stated" as *leave the stored
 * schedule alone*. A write that states them is stating a whole schedule, so `null`/`[]` here mean
 * the value the chosen frequency does not use, not "say nothing" - which is why the two are
 * separate request shapes rather than one with optional fields
 * ({@link UpdateSupplierPresetScheduleRequest} and
 * {@link UpdateSupplierPresetKeepingScheduleRequest}).
 */
export interface SupplierSearchScheduleRequest {
  readonly searchFrequency: SupplierSearchFrequency;
  /** The chosen weekdays in the server's Monday-to-Sunday order; empty for a daily schedule. */
  readonly searchDaysOfWeek: readonly string[];
  /** The fortnight start date as `YYYY-MM-DD`, and `null` for every non-fortnightly schedule. */
  readonly fortnightStartDate: string | null;
}

/**
 * What a create submits. A new preset is created enabled, so there is no `isEnabled` - but it does
 * state `searchPrices` (issue #1077), because adding a supplier whose searches cost money without
 * switching them on is a real thing to want. The server defaults an omitted value to `true`; this
 * client always sends it, so the dialog's own switch is what decides.
 */
export interface CreateSupplierPresetRequest extends SupplierSearchScheduleRequest {
  readonly name: string;
  readonly url: string | null;
  readonly searchPrices: boolean;
  readonly servesNationwide: boolean;
  readonly servedStates: readonly string[];
  readonly pricesIncludeGst: boolean;
  readonly priceConnector: SupplierPriceConnector | null;
  readonly priceConnectorKey: string | null;
}

/**
 * The fields every edit states: the whole resource apart from the schedule, so enabling and
 * disabling is this call - and so is switching price searching on and off (issue #1077). Both flags
 * are required by the server, so every caller has to state the one it is not changing rather than
 * letting it default.
 */
export interface SupplierPresetEditFields {
  readonly name: string;
  readonly url: string | null;
  readonly isEnabled: boolean;
  readonly searchPrices: boolean;
  readonly servesNationwide: boolean;
  readonly servedStates: readonly string[];
  readonly pricesIncludeGst: boolean;
  readonly priceConnector: SupplierPriceConnector | null;
  readonly priceConnectorKey: string | null;
}

/**
 * What an edit that *decides* the schedule submits (issue #1109): the dialog's Save, where a person
 * has the three fields in front of them and the values on screen are the ones to store.
 */
export interface UpdateSupplierPresetScheduleRequest
  extends SupplierPresetEditFields,
    SupplierSearchScheduleRequest {}

/**
 * What an edit that decides something *other* than the schedule submits (issue #1109): the list's
 * two toggle confirmations, which change one flag.
 *
 * **The three schedule fields are absent on purpose, and the `never` is what says so.** The server
 * reads a `PUT` that states none of them as *keep the stored schedule*, and leaves
 * `scheduleEffectiveFromUtc` where it is unless that same request turned **Search prices** from off
 * to on. Re-sending the schedule from a list row read some time ago would instead submit it as a
 * schedule edit: a timetable another Super Admin (or another tab) has saved since would be
 * overwritten by the stale one, its freshness limit recalculated and its effective-from boundary
 * advanced - making every one of that supplier's products due again - by a button that only said
 * *Disable*. Omitting them makes the stale value unsendable rather than merely unlikely to be sent.
 */
export interface UpdateSupplierPresetKeepingScheduleRequest extends SupplierPresetEditFields {
  readonly searchFrequency?: never;
  readonly searchDaysOfWeek?: never;
  readonly fortnightStartDate?: never;
}

/** Either kind of edit: one states a whole schedule, the other states none of it. */
export type UpdateSupplierPresetRequest =
  | UpdateSupplierPresetScheduleRequest
  | UpdateSupplierPresetKeepingScheduleRequest;

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
