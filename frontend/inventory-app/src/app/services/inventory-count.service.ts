import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { InventoryCountApplyRequest, InventoryCountApplyResult } from '../models/models';
import { ConfigService } from './config.service';

/** The Take Inventory page's Apply action (issue #245). */
@Injectable({ providedIn: 'root' })
export class InventoryCountService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  apply(productId: number, request: InventoryCountApplyRequest): Observable<InventoryCountApplyResult> {
    return this.http.post<InventoryCountApplyResult>(`${this.baseUrl}/${productId}/inventory-count/apply`, request);
  }
}
