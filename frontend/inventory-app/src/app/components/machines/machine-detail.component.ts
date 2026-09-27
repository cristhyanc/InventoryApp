import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import {
  Machine,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventMatchStatus,
  NayaxStockEventPreview,
  Product,
  StockAdjustmentReason,
  StockAdjustmentDto
} from '../../models/models';

@Component({
  selector: 'app-machine-detail',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './machine-detail.component.html'
})
export class MachineDetailComponent implements OnInit {
  machine$!: Observable<Machine | null>;
  products$!: Observable<Product[]>;
  loading$ = new BehaviorSubject(true);
  error$ = new BehaviorSubject('');

  syncPreview$ = new BehaviorSubject<NayaxMachineStockSyncPreview | null>(null);
  syncing$ = new BehaviorSubject(false);
  applying$ = new BehaviorSubject(false);
  selectedEventIds = new Set<number>();

  readonly MatchStatus = NayaxStockEventMatchStatus;

  constructor(
    private route: ActivatedRoute,
    private machineService: MachineService,
    private stockService: StockService,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    const machineId$ = this.route.paramMap.pipe(map((params) => Number(params.get('id'))));

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

        return this.machineService.getProducts(machineId).pipe(
          catchError(() => {
            this.error$.next('Failed to load products for this machine.');
            return of([] as Product[]);
          })
        );
      })
    );
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

  isReadyToApply(event: NayaxStockEventPreview): boolean {
    return event.matchStatus === NayaxStockEventMatchStatus.Matched &&
      (event.parsedQuantity ?? 0) > 0 &&
      !event.isInsufficientStock;
  }

  isEventSelected(eventId: number): boolean {
    return this.selectedEventIds.has(eventId);
  }

  toggleEventSelection(eventId: number, checked: boolean): void {
    if (checked) {
      this.selectedEventIds.add(eventId);
    } else {
      this.selectedEventIds.delete(eventId);
    }
  }

  syncRestock(machineId: number | null | undefined): void {
    if (!machineId) {
      return;
    }

    this.syncing$.next(true);
    this.machineService.syncRestock(machineId).subscribe({
      next: (preview) => {
        this.syncing$.next(false);
        this.syncPreview$.next(preview);
        this.selectedEventIds = new Set(
          preview.events.filter((e) => this.isReadyToApply(e)).map((e) => e.id)
        );
        if (preview.message) {
          this.toastService.success(preview.message);
        } else {
          this.toastService.success(`${preview.newEventCount} new Nayax alert(s) found.`);
        }
      },
      error: () => {
        this.syncing$.next(false);
        this.toastService.error('Failed to sync Nayax stock-adjustment alerts.');
      }
    });
  }

  applySelectedEvents(machineId: number | null | undefined): void {
    if (!machineId || this.selectedEventIds.size === 0) {
      return;
    }

    this.applying$.next(true);
    this.machineService.applySyncRestock(machineId, [...this.selectedEventIds]).subscribe({
      next: (response) => {
        this.applying$.next(false);
        const appliedCount = response.results.filter((r) => r.outcome === NayaxStockEventApplyOutcome.Applied).length;
        const failedCount = response.results.length - appliedCount;
        if (appliedCount > 0) {
          this.toastService.success(`Applied ${appliedCount} Nayax stock-adjustment event(s).`);
        }
        if (failedCount > 0) {
          this.toastService.warning(`${failedCount} event(s) could not be applied and remain for review.`);
        }
        this.selectedEventIds.clear();
        this.syncRestock(machineId);
        this.products$ = this.machineService.getProducts(machineId).pipe(
          catchError(() => {
            this.error$.next('Failed to load products for this machine.');
            return of([] as Product[]);
          })
        );
      },
      error: () => {
        this.applying$.next(false);
        this.toastService.error('Failed to apply the selected Nayax stock-adjustment events.');
      }
    });
  }
}
