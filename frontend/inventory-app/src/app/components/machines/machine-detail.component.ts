import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, Subscription, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { BreadcrumbService } from '../../layout/breadcrumbs/breadcrumb.service';
import { MachineRestockSyncComponent } from './machine-restock-sync/machine-restock-sync.component';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  Machine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentDto
} from '../../models/models';

@Component({
  selector: 'app-machine-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, MachineRestockSyncComponent, NayaxStatusBannerComponent],
  templateUrl: './machine-detail.component.html'
})
export class MachineDetailComponent implements OnInit, OnDestroy {
  machine$!: Observable<Machine | null>;
  products$!: Observable<Product[]>;
  loading$ = new BehaviorSubject(true);
  error$ = new BehaviorSubject('');

  /**
   * Whether a support session is active (issue #658): Restock is hidden while it is, because a
   * session grants `InventoryOperate` - the capability this write would otherwise be gated on -
   * and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private machineId: number | null = null;
  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private route: ActivatedRoute,
    private machineService: MachineService,
    private stockService: StockService,
    private toastService: ToastService,
    private breadcrumbService: BreadcrumbService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

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
      }),
      // The breadcrumb's live label for this page (issue #457), from the machine data this page
      // already requested for itself - never a second request made just for the breadcrumb.
      tap((machine) => this.breadcrumbService.setCurrentPageLabel(machine?.machineName ?? null))
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

    if (stockPercentage < 30) return 'badge-danger';
    if (stockPercentage < 80) return 'badge-warning';
    return 'badge-success';
  }

  restockProduct(product: Product, machine: Machine | null, qty?: string | number, qtyInput?: HTMLInputElement): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
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
