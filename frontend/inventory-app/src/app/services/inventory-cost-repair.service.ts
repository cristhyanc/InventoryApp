import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/** A completed sale the replay cannot cost, as the repair preview reports it (issue #359). */
export interface InventoryCostRepairUncostableSale {
  transactionId: number;
  authorizationTime: string;
}

/** One fatal data-quality issue a repair would leave behind, as the preview reports it. */
export interface InventoryCostDataQualityIssue {
  code: string;
  message: string;
}

/** What the operator proposes: a costing-only acquisition for one product (issue #359). */
export interface InventoryCostRepairRequest {
  productId: number;
  effectiveAt: string;
  quantity: number;
  unitCost: number;
  reason: string;
}

/**
 * What applying the proposed repair would do, computed without persisting anything. Matches
 * `Inventory.Application.Costing.InventoryCostRepairPreview` field-for-field.
 */
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
  firstUncostableSale: InventoryCostRepairUncostableSale | null;
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

  preview(request: InventoryCostRepairRequest): Observable<InventoryCostRepairPreview> {
    return this.http.post<InventoryCostRepairPreview>(`${this.baseUrl}/preview`, request);
  }

  /** Applies exactly the previewed proposal, echoing back the `ledgerFingerprint` it reported. */
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
