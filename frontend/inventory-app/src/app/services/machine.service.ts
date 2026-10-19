import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Machine,
  MachineLatestSales,
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
