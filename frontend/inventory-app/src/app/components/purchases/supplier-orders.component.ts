import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ListLoadState } from '../shared/list-load-state';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupplierOrder, SupplierOrderStatus } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';

/** '' means "every open status"; 0/1 match `SupplierOrderStatus.Ordered`/`PartiallyReceived`. */
export type SupplierOrderStatusFilter = '' | 0 | 1;

@Component({
  selector: 'app-supplier-orders',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './supplier-orders.component.html'
})
export class SupplierOrdersComponent implements OnInit, OnDestroy {
  orders: SupplierOrder[] = [];
  readonly loadState = new ListLoadState();

  search = '';
  statusFilter: SupplierOrderStatusFilter = '';

  /**
   * Whether a support session is active (issue #657, extending #554): Receive / Create Purchase
   * and Cancel are hidden while it is, because a session grants `PurchasingOperate` - the
   * capability this page would otherwise gate those on - and the backend refuses the write itself
   * only by its read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly supplierOrderService: SupplierOrderService,
    private readonly router: Router,
    private readonly toastService: ToastService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.loadOrders();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
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
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
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
    // Index 4 is issue #974's explicit completion. It is deliberately neither "Cancelled" nor
    // "Received": goods really were delivered and recorded, and part of the order never arrived.
    return (
      ['Ordered', 'Partially received', 'Received', 'Cancelled', 'Completed — partially delivered'][status] ?? 'Unknown'
    );
  }

  /**
   * Whether this order is still open, which is what the Receive and Cancel actions require.
   * `GET api/supplierorders` returns only open orders, so this is the defensive half of that
   * contract rather than a second rule - and it is written as "is one of the two open statuses"
   * instead of "is not one of the closed ones", so a closed status added later is excluded by
   * default rather than offered a write.
   */
  isOpen(order: SupplierOrder): boolean {
    return order.status === SupplierOrderStatus.Ordered || order.status === SupplierOrderStatus.PartiallyReceived;
  }

  /**
   * What this line records as never delivered, from the server's own figure. An older response that
   * carries none reads as nothing undelivered, which is what every order but a completed one is.
   */
  undelivered(line: SupplierOrder['lines'][number]): number {
    return line.undeliveredQuantity ?? 0;
  }

  /** Whether any line of this order records a balance that never arrived. */
  hasUndelivered(order: SupplierOrder): boolean {
    return order.lines.some((line) => this.undelivered(line) > 0);
  }
}
