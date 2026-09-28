import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { MachineRestockSyncComponent } from './machine-restock-sync/machine-restock-sync.component';
import {
  Machine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentDto
} from '../../models/models';

@Component({
  selector: 'app-machine-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, MachineRestockSyncComponent],
  templateUrl: './machine-detail.component.html'
})
export class MachineDetailComponent implements OnInit {
  machine$!: Observable<Machine | null>;
  products$!: Observable<Product[]>;
  loading$ = new BehaviorSubject(true);
  error$ = new BehaviorSubject('');

  private machineId: number | null = null;

  constructor(
    private route: ActivatedRoute,
    private machineService: MachineService,
    private stockService: StockService,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    const machineId$ = this.route.paramMap.pipe(
      map((params) => Number(params.get('id'))),
      tap((machineId) => {
        this.machineId = !machineId || isNaN(machineId) ? null : machineId;
      })
    );

    this.machine$ = machineId$.pipe(
      tap(() => {
        this.loading$.next(true);
        this.error$.next('');
      }),
      switchMap((machineId) => {
        if (!machineId || isNaN(machineId)) {
          this.error$.next('Invalid machine id.');
          this.loading$.next(false);
          return of(null);
        }

        return this.machineService.get(machineId).pipe(
          tap(() => this.loading$.next(false)),
          catchError(() => {
            this.error$.next('Failed to load machine details.');
            this.loading$.next(false);
            return of(null);
          })
        );
      })
    );

    this.products$ = machineId$.pipe(
      switchMap((machineId) => {
        if (!machineId || isNaN(machineId)) {
          return of([] as Product[]);
        }

        return this.loadProducts(machineId);
      })
    );
  }

  /**
   * Reloads the machine's products. Called when the Sync Restock panel reports that it applied a
   * Nayax stock-adjustment event, because that changes storage quantities shown in this table.
   */
  refreshProducts(): void {
    if (!this.machineId) {
      return;
    }

    this.products$ = this.loadProducts(this.machineId);
  }

  stockClass(product: Product): string {
    const maxStock = product.maxStockInMachine ?? 0;
    const stockPercentage = maxStock > 0
      ? ((product.quantityInStock ?? 0) / maxStock) * 100
      : 100;

    if (stockPercentage < 30) return 'bg-red-100 text-red-700';
    if (stockPercentage < 80) return 'bg-yellow-100 text-yellow-700';
    return 'bg-green-100 text-green-700';
  }

  restockProduct(product: Product, machine: Machine | null, qty?: string | number, qtyInput?: HTMLInputElement): void {
    const defaultQty = (product.maxStockInMachine ?? 0) - (product.quantityInStock ?? 0);
    const qtyNum = Math.trunc(Number(qty ?? defaultQty));
    if (!product || qtyNum <= 0) {
      this.toastService.warning('Quantity must be greater than 0');
      return;
    }

    const payload: StockAdjustmentDto = {
      quantityChange: qtyNum*-1,
      reason: StockAdjustmentReason.MachineRefill,
      machineId: machine?.machineID ?? null,
      notes: `${machine?.machineName ?? ''} - ${machine?.machineID ?? ''} - Machine restock`
    };

    this.stockService.adjust(product.id, payload).subscribe({
      next: () => {
        this.toastService.success(`${product.name} restocked by ${qtyNum}`);
        if (qtyInput) {
          qtyInput.value = '0';
        }
      },
      error: (x) => {
        const message = typeof x.error === 'string'
          ? x.error
          : x.error?.message ?? x.error?.title;
        this.toastService.error(message ?? 'Failed to restock product');
      }
    });
  }

  private loadProducts(machineId: number): Observable<Product[]> {
    return this.machineService.getProducts(machineId).pipe(
      catchError(() => {
        this.error$.next('Failed to load products for this machine.');
        return of([] as Product[]);
      })
    );
  }
}
