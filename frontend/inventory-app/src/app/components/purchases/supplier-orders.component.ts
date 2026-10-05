import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ListLoadState } from '../shared/list-load-state';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupplierOrder } from '../../models/models';

/** '' means "every open status"; 0/1 match `SupplierOrderStatus.Ordered`/`PartiallyReceived`. */
export type SupplierOrderStatusFilter = '' | 0 | 1;

@Component({
  selector: 'app-supplier-orders',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './supplier-orders.component.html'
})
export class SupplierOrdersComponent implements OnInit {
  orders: SupplierOrder[] = [];
  readonly loadState = new ListLoadState();

  search = '';
  statusFilter: SupplierOrderStatusFilter = '';

  constructor(
    private readonly supplierOrderService: SupplierOrderService,
    private readonly router: Router,
    private readonly toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.loadOrders();
  }

  loadOrders(): void {
    const token = this.loadState.start();
    this.supplierOrderService.getActive().subscribe({
      next: (orders) => {
        if (this.loadState.isCurrent(token)) {
          this.orders = orders;
        }
        this.loadState.succeed(token);
      },
      error: () => {
        this.loadState.fail(token);
        this.toastService.error('Failed to load supplier orders.');
      }
    });
  }

  // GET api/supplierorders already returns only Ordered/PartiallyReceived orders, so filtering
  // here is purely client-side presentation over that already-active set.
  get filteredOrders(): SupplierOrder[] {
    const term = this.search.trim().toLowerCase();
    return this.orders.filter((order) => {
      if (this.statusFilter !== '' && order.status !== this.statusFilter) {
        return false;
      }
      if (!term) {
        return true;
      }
      const supplierName = order.supplier?.name?.toLowerCase() ?? '';
      const reference = order.reference?.toLowerCase() ?? '';
      return supplierName.includes(term) || reference.includes(term);
    });
  }

  get hasActiveFilters(): boolean {
    return this.search.trim() !== '' || this.statusFilter !== '';
  }

  resetFilters(): void {
    this.search = '';
    this.statusFilter = '';
  }

  cancelOrder(order: SupplierOrder): void {
    this.supplierOrderService.cancel(order.id).subscribe({
      next: () => {
        this.toastService.success('Supplier order cancelled.');
        this.loadOrders();
      },
      error: () => this.toastService.error('Unable to cancel this supplier order.')
    });
  }

  receiveOrder(order: SupplierOrder): void {
    this.router.navigate(['/purchases/new'], { queryParams: { supplierOrderId: order.id } });
  }

  orderStatus(status: number): string {
    return ['Ordered', 'Partially received', 'Received', 'Cancelled'][status] ?? 'Unknown';
  }
}
