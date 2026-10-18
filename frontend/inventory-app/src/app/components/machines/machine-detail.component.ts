import { Component, ElementRef, OnDestroy, OnInit, QueryList, ViewChildren } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, Subscription, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { BreadcrumbService } from '../../layout/breadcrumbs/breadcrumb.service';
import { MachineRestockSyncComponent } from './machine-restock-sync/machine-restock-sync.component';
import { MachineLatestSalesComponent } from './machine-latest-sales/machine-latest-sales.component';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import {
  Machine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentDto
} from '../../models/models';

/**
 * The two content tabs of the machine-details page (issue #768), in the order the owner asked for
 * them. Sales is first and is the tab every machine opens on.
 */
export type MachineDetailTab = 'sales' | 'products';

/**
 * The four distinct states of the Products panel's one read (issue #768), which the template keeps
 * apart because three of them would otherwise be presented as "this machine has no products":
 * nobody has opened the tab yet, the read is in flight, it succeeded (with however many rows it
 * returned, including none), or it failed.
 */
export type MachineProductsState = 'not-requested' | 'loading' | 'loaded' | 'failed';

@Component({
  selector: 'app-machine-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MachineRestockSyncComponent,
    MachineLatestSalesComponent,
    NayaxStatusBannerComponent
  ],
  templateUrl: './machine-detail.component.html'
})
export class MachineDetailComponent implements OnInit, OnDestroy {
  readonly machine$: Observable<Machine | null>;

  /**
   * The machine's product table, held as the rows this page has actually read rather than as an
   * observable the template subscribes to (issue #768). The table and its row count are two
   * bindings over one read: an unshared cold `HttpClient` observable bound twice would make the
   * same machine-products call twice, and the row count would be read from a different subscription
   * from the rows. One subscription, owned here, is what makes "opening Products reads once" true
   * of the rendered page and not only of this component's intent.
   */
  products: Product[] = [];

  /** Which of the panel's four states the product table is in; never inferred from `products`. */
  productsState: MachineProductsState = 'not-requested';

  loading$ = new BehaviorSubject(true);
  error$ = new BehaviorSubject('');

  /** The tabs, in display order. Sales first, by the owner's 18 October 2026 decision. */
  readonly tabs: readonly MachineDetailTab[] = ['sales', 'products'];

  /** Sales on arrival, and again on arriving at a different machine. */
  activeTab: MachineDetailTab = 'sales';

  /**
   * Whether a support session is active (issue #658): Restock is hidden while it is, because a
   * session grants `InventoryOperate` - the capability this write would otherwise be gated on -
   * and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #699): the
   * revenue/direct-profit summary cards and the products table's Estimated Card Profit/50% Card
   * Margin Price columns are hidden without it, on the frontend-level check this page applies
   * ahead of the machine endpoints redacting those figures themselves (#697/#698). Starts
   * `true`, the fail-open default every other capability-gated control in this codebase uses
   * while the capability has not been read yet.
   */
  canViewFinancials = true;

  @ViewChildren('tabButton') private tabButtons?: QueryList<ElementRef<HTMLButtonElement>>;

  private machineId: number | null = null;

  private readonly sessionActiveSubscription: Subscription;
  private readonly financialsAccessSubscription: Subscription;
  private routeSubscription?: Subscription;

  /** The one in-flight product read, so a superseded one can be cancelled rather than raced. */
  private productsSubscription?: Subscription;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly machineService: MachineService,
    private readonly stockService: StockService,
    private readonly toastService: ToastService,
    private readonly breadcrumbService: BreadcrumbService,
    supportSession: SupportSessionService,
    access: AccessService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.financialsAccessSubscription = access
      .allows('Dashboard.ViewFinancials')
      .subscribe((allowed) => (this.canViewFinancials = allowed));

    this.machine$ = this.route.paramMap.pipe(
      map((params) => Number(params.get('id'))),
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
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
    this.financialsAccessSubscription.unsubscribe();
    this.routeSubscription?.unsubscribe();
    this.cancelProductsRead();
  }

  ngOnInit(): void {
    // The route's own machine id, resolved eagerly rather than as a side effect of somebody
    // subscribing to `machine$`: the tabs' state and the lazy product read both depend on it, and
    // neither may wait for a template binding to have run (issue #768).
    this.routeSubscription = this.route.paramMap.subscribe((params) => {
      const machineId = Number(params.get('id'));
      this.machineId = !machineId || isNaN(machineId) ? null : machineId;

      // Every machine opens on Sales, and its products are not fetched until somebody opens the
      // Products tab. An in-flight read for the machine being navigated away from is cancelled and
      // the rows are cleared here, at the moment the machine changes, so the previous machine's
      // products can never appear under this machine's heading or arrive late into this table.
      this.activeTab = 'sales';
      this.cancelProductsRead();
      this.products = [];
      this.productsState = 'not-requested';
    });
  }

  /**
   * Shows one tab, and loads the machine's products the first time Products is opened for this
   * machine (issue #768). The read is tied to opening that tab rather than to arriving on the page,
   * so a page that opens on Sales makes one Nayax call and not two - and it happens once, so
   * switching tabs never repeats it.
   */
  openTab(tab: MachineDetailTab): void {
    this.activeTab = tab;

    if (tab !== 'products' || this.productsState !== 'not-requested' || !this.machineId) {
      return;
    }

    this.loadProducts(this.machineId);
  }

  /**
   * Roving-tabindex keyboard navigation for the tab list, as the ARIA tabs pattern specifies: the
   * arrow keys move between tabs and select the one they land on, Home and End jump to the ends,
   * and only the selected tab is in the page's tab order.
   */
  onTabKeydown(event: KeyboardEvent, index: number): void {
    const last = this.tabs.length - 1;
    let target: number;
    switch (event.key) {
      case 'ArrowRight':
        target = index === last ? 0 : index + 1;
        break;
      case 'ArrowLeft':
        target = index === 0 ? last : index - 1;
        break;
      case 'Home':
        target = 0;
        break;
      case 'End':
        target = last;
        break;
      default:
        return;
    }

    event.preventDefault();
    this.openTab(this.tabs[target]);
    this.tabButtons?.get(target)?.nativeElement.focus();
  }

  /** The tab's visible label and its accessible name; one place, so they cannot disagree. */
  tabLabel(tab: MachineDetailTab): string {
    return tab === 'sales' ? 'Sales' : 'Products';
  }

  /**
   * Reloads the machine's products. Called when the Sync Restock panel reports that it applied a
   * Nayax stock-adjustment event, because that changes storage quantities shown in this table. It
   * does nothing while the Products tab has never been opened for this machine: there is no table
   * on screen to refresh, and opening the tab later reads the current quantities anyway.
   */
  refreshProducts(): void {
    if (!this.machineId || this.productsState === 'not-requested') {
      return;
    }

    this.loadProducts(this.machineId);
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

  /**
   * Reads one machine's products exactly once per call, into this component's own state. A
   * superseded read - a refresh, or a different machine - is cancelled rather than left to answer
   * into a table that has moved on, and a failure is its own state in the panel rather than the
   * page-level error line, so a failed read is never presented as a machine with no products.
   */
  private loadProducts(machineId: number): void {
    this.cancelProductsRead();
    this.productsState = 'loading';

    this.productsSubscription = this.machineService.getProducts(machineId).subscribe({
      next: (products) => {
        this.products = products;
        this.productsState = 'loaded';
      },
      error: () => {
        this.products = [];
        this.productsState = 'failed';
      }
    });
  }

  private cancelProductsRead(): void {
    this.productsSubscription?.unsubscribe();
    this.productsSubscription = undefined;
  }
}
