import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, startWith, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { Machine, Product, StockAdjustmentReason, StockAdjustmentDto } from '../../models/models';

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
  private productsRefresh = new BehaviorSubject<void>(undefined);

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

        return this.productsRefresh.pipe(
          startWith(undefined),
          switchMap(() =>
            this.machineService.getProducts(machineId).pipe(
              catchError(() => {
                this.error$.next('Failed to load products for this machine.');
                return of([] as Product[]);
              })
            )
          )
        );
      })
    );
  }

  restockProduct(product: Product, machine: Machine | null, qty?: string | number): void {
    const defaultQty = (product.maxStockInMachine ?? 0) - (product.quantityInStock ?? 0);
    const qtyNum = Math.trunc(Number(qty ?? defaultQty));
    if (!product || qtyNum <= 0) {
      this.toastService.warning('Quantity must be greater than 0');
      return;
    }

    const payload: StockAdjustmentDto = {
      quantityChange: qtyNum,
      reason: StockAdjustmentReason.Other,
      notes: `${machine?.machineName ?? ''} - ${machine?.machineID ?? ''} - Machine restock`
    };

    this.stockService.adjust(product.id, payload).subscribe({
      next: () => {
        this.toastService.success(`${product.name} restocked by ${qtyNum}`);
        this.productsRefresh.next();
      },
      error: () => {
        this.toastService.error('Failed to create restock adjustment');
      }
    });
  }
}
