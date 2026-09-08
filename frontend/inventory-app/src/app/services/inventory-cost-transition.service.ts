import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export enum InventoryCostBaselineSource {
  ManualAuthoritative = 1,
  ManualEstimated = 2
}

export interface InventoryCostTransitionMachineStock {
  machineId: number;
  machineName: string;
  stockQuantity: number;
  source: string;
}

export interface InventoryCostTransitionPreview {
  previewId: string;
  productId: number;
  productName: string;
  homeStockQuantity: number;
  machineStocks: InventoryCostTransitionMachineStock[];
  machineStockQuantity: number;
  openingCostingQuantity: number;
  averageUnitCost: number;
  inventoryValue: number;
  cutoffAt: string;
  costSource: InventoryCostBaselineSource;
  legacyReplayedPhysicalQuantity: number;
  legacyPhysicalDiscrepancy: number;
  dataQualityNote: string;
}

export interface InventoryCostTransitionBatchPreview {
  previewId: string;
  cutoffAt: string;
  costSource: InventoryCostBaselineSource;
  products: InventoryCostTransitionPreview[];
  productCount: number;
  homeStockQuantity: number;
  machineStockQuantity: number;
  openingCostingQuantity: number;
  inventoryValue: number;
}

@Injectable({ providedIn: 'root' })
export class InventoryCostTransitionService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/admin/inventory-cost-transition`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  preview(
    productId: number,
    averageUnitCost: number,
    costSource: InventoryCostBaselineSource
  ): Observable<InventoryCostTransitionPreview> {
    return this.http.post<InventoryCostTransitionPreview>(`${this.baseUrl}/preview`, {
      productId,
      averageUnitCost,
      costSource
    });
  }

  apply(preview: InventoryCostTransitionPreview): Observable<InventoryCostTransitionPreview> {
    return this.http.post<InventoryCostTransitionPreview>(`${this.baseUrl}/apply`, {
      previewId: preview.previewId,
      confirmed: true
    });
  }

  previewAll(costSource: InventoryCostBaselineSource): Observable<InventoryCostTransitionBatchPreview> {
    return this.http.post<InventoryCostTransitionBatchPreview>(`${this.baseUrl}/preview-all`, { costSource });
  }

  applyAll(preview: InventoryCostTransitionBatchPreview): Observable<InventoryCostTransitionBatchPreview> {
    return this.http.post<InventoryCostTransitionBatchPreview>(`${this.baseUrl}/apply-all`, {
      previewId: preview.previewId,
      confirmed: true
    });
  }
}
