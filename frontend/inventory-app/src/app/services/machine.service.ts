import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Machine,
  MachineLatestSales,
  MachineRestockSyncReminderResponse,
  NayaxDuplicateResolutionChoice,
  NayaxMachineStockApplyResponse,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyResult,
  Product
} from '../models/models';
import { ConfigService } from './config.service';

@Injectable({
  providedIn: 'root'
})
export class MachineService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/machines`;
  }

  constructor(private http: HttpClient, private config: ConfigService) { }

  getAll(): Observable<Machine[]> {
    return this.http.get<Machine[]>(this.baseUrl);
  }

  /**
   * The Dashboard's restock-sync reminder (issue #891): which provider-named machines have not had
   * a fully successful Sync Restock in the last three business days, and which still hold
   * unresolved restock events.
   *
   * A pure read of local state - it makes no Nayax request, imports nothing and acknowledges
   * nothing - which is why the Dashboard may call it on every load. Its own route rather than the
   * machine fleet's, because it is about the whole business rather than one machine.
   */
  getRestockSyncReminder(): Observable<MachineRestockSyncReminderResponse> {
    return this.http.get<MachineRestockSyncReminderResponse>(
      `${this.config.apiBaseUrl.replace(/\/$/, '')}/machine-restock-sync/reminder`
    );
  }

  get(id: number): Observable<Machine> {
    return this.http.get<Machine>(`${this.baseUrl}/${id}`);
  }

  getProducts(id: number): Observable<Product[]> {
    return this.http.get<Product[]>(`${this.baseUrl}/${id}/products`);
  }

  /**
   * The machine's latest Nayax sales (issue #768): the provider's own rolling latest-sales window,
   * newest first. A pure read - it imports nothing and persists nothing - and an unconfigured or
   * unusable provider answers `200` with `isAvailable: false` rather than an error status.
   */
  getLatestSales(id: number): Observable<MachineLatestSales> {
    return this.http.get<MachineLatestSales>(`${this.baseUrl}/${id}/latest-sales`);
  }

  /**
   * @param fromDateIso The Sync Restock working list's From date filter (issue #206), as a UTC
   * instant (`Date.toISOString()`), or `null` to see the complete unprocessed history.
   */
  syncRestock(
    id: number, fromDateIso: string | null, includeReconciled: boolean
  ): Observable<NayaxMachineStockSyncPreview> {
    let params = new HttpParams().set('includeReconciled', includeReconciled);
    if (fromDateIso) {
      params = params.set('fromDate', fromDateIso);
    }
    return this.http.post<NayaxMachineStockSyncPreview>(`${this.baseUrl}/${id}/sync-restock`, {}, { params });
  }

  applySyncRestock(id: number, eventIds: number[]): Observable<NayaxMachineStockApplyResponse> {
    return this.http.post<NayaxMachineStockApplyResponse>(`${this.baseUrl}/${id}/sync-restock/apply`, { eventIds });
  }

  resolveSyncRestockDuplicate(
    id: number,
    eventId: number,
    resolution: NayaxDuplicateResolutionChoice
  ): Observable<NayaxStockEventApplyResult> {
    return this.http.post<NayaxStockEventApplyResult>(
      `${this.baseUrl}/${id}/sync-restock/resolve-duplicate`,
      { eventId, resolution }
    );
  }

  /**
   * Resolves every listed Sync Restock event as "already recorded manually" in one explicit bulk
   * action (issue #242), whether or not the app flagged it as a possible duplicate.
   */
  resolveSyncRestockManually(id: number, eventIds: number[]): Observable<NayaxMachineStockApplyResponse> {
    return this.http.post<NayaxMachineStockApplyResponse>(
      `${this.baseUrl}/${id}/sync-restock/resolve-manual`,
      { eventIds }
    );
  }
}
