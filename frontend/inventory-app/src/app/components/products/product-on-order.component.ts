import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { ListLoadState } from '../shared/list-load-state';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupplierOrder } from '../../models/models';

@Component({
  selector: 'app-product-on-order',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-on-order.component.html'
})
export class ProductOnOrderComponent implements OnInit {
  orders: SupplierOrder[] = [];
  readonly loadState = new ListLoadState();

  constructor(
    private supplierOrderService: SupplierOrderService,
    private router: Router,
    private toastService: ToastService
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
    this.router.navigate(['/receipts/new'], { queryParams: { supplierOrderId: order.id } });
  }

  orderStatus(status: number): string {
    return ['Ordered', 'Partially received', 'Received', 'Cancelled'][status] ?? 'Unknown';
  }
}
