import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/** A completed sale the replay cannot cost, as the repair preview reports it (issue #359). */
export interface UncostableSale {
  transactionId: number;
  authorizationTime: string;
}

/** One fatal data-quality issue the replay still reports, as the preview/history report it. */
export interface InventoryCostDataQualityIssue {
  code: string;
  message: string;
}

/** What applying the proposed repair would do, computed without persisting anything (issue #359). */
export interface InventoryCostRepairPreview {
  productId: number;
  productName: string;
  effectiveAt: string;
  quantity: number;
  unitCost: number;
  totalValue: number;
  reason: string;
  costingQuantityBefore: number;
  inventoryValueBefore: number;
  costingQuantityAfter: number;
  inventoryValueAfter: number;
  averageUnitCostAfter: number | null;
  firstUncostableSale: UncostableSale | null;
  replaysBeforeFirstUncostableSale: boolean;
  projectedCostingQuantity: number;
  projectedInventoryValue: number;
  projectedAverageUnitCost: number | null;
  remainingFatalIssues: InventoryCostDataQualityIssue[];
  ledgerFingerprint: string;
}

/** One stored costing repair, as the history query and the apply result report it. */
export interface InventoryCostRepairRecord {
  id: number;
  productId: number;
  effectiveAt: string;
  quantity: number;
  unitCost: number;
  totalValue: number;
  reason: string;
  createdAt: string;
  createdByDirectoryTenantId: string;
  createdByObjectId: string;
}

/** The persisted repair and the product's rebuilt costing position after it. */
export interface InventoryCostRepairApplied {
  repair: InventoryCostRepairRecord;
  costingQuantity: number;
  inventoryValue: number;
  averageUnitCost: number | null;
  recostedSaleCount: number;
}

@Injectable({ providedIn: 'root' })
export class InventoryCostRepairService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/admin/inventory-cost-repair`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  preview(
    productId: number,
    effectiveAt: string,
    quantity: number,
    unitCost: number,
    reason: string
  ): Observable<InventoryCostRepairPreview> {
    return this.http.post<InventoryCostRepairPreview>(`${this.baseUrl}/preview`, {
      productId,
      effectiveAt,
      quantity,
      unitCost,
      reason
    });
  }

  apply(preview: InventoryCostRepairPreview): Observable<InventoryCostRepairApplied> {
    return this.http.post<InventoryCostRepairApplied>(`${this.baseUrl}/apply`, {
      productId: preview.productId,
      effectiveAt: preview.effectiveAt,
      quantity: preview.quantity,
      unitCost: preview.unitCost,
      reason: preview.reason,
      ledgerFingerprint: preview.ledgerFingerprint
    });
  }

  history(productId: number): Observable<InventoryCostRepairRecord[]> {
    return this.http.get<InventoryCostRepairRecord[]>(`${this.baseUrl}/${productId}`);
  }
}
