import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { StockService } from '../../services/stock.service';
import { ProductService } from '../../services/product.service';
import { LocalMachineService } from '../../services/local-machine.service';
import {
  LocalMachine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentSource,
  StockHistoryPage
} from '../../models/models';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { StockAdjustmentFormComponent } from './stock-adjustment-form/stock-adjustment-form.component';

/**
 * The global Stock History page (issue #384), routed at `/stock-history` and also reached from the
 * preserved product entry point `/products/:id/stock`, which opens this same page with that product
 * preselected.
 *
 * It lists stock movements across every product the business owns, newest first, and narrows them by
 * product, business calendar date range, reason, machine and source. One bounded request serves a
 * page: the page never loads every product to fan out a history request per product, and the server
 * caps the page size whatever this page asks for.
 *
 * The page is a composition boundary (docs/architecture.md § Page composition boundary): it owns the
 * route parameters, the filter state, the paging state and its own loading/empty/error states, and
 * delegates the manual-adjustment workflow to {@link StockAdjustmentFormComponent}. It applies no
 * inventory or costing rule of its own - date interpretation, ordering, bounding and every stock
 * mutation stay on the backend.
 *
 * **The machine filter is the business's own machines, and only the source filter depends on the
 * provider** (issue #746, G2b of issue #329, revising issue #728's rule here):
 *
 * - The **machine** filter and the adjustment form's machine field are read from
 *   `/api/local-machines` ({@link LocalMachineService}), not from the live Nayax fleet, so they work
 *   with no provider at all. The filter is offered once the business has an **active** machine, and
 *   lists its **deactivated** ones as well, so a movement recorded against a retired machine can
 *   still be found and named; the machine column names them from the same list. A failed read is
 *   reported where the filter was and an outstanding one says it is loading - neither is presented
 *   as "all machines".
 * - The **"Nayax Sync Restock" source** option needs only Nayax to be part of this business: the
 *   movements it selects are already recorded locally. A connection that merely needs attention
 *   keeps it, and the table keeps labelling such a movement whatever the filter offers.
 */
@Component({
  selector: 'app-stock-history-page',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, BusinessDateTimePipe, StockAdjustmentFormComponent],
  templateUrl: './stock-history-page.component.html'
})
export class StockHistoryPageComponent implements OnInit {
  products: Product[] = [];

  /**
   * Every machine this business has recorded, active and inactive alike (issue #746). The filter
   * and the machine column list all of them, because a movement recorded against a machine that
   * has since been retired must stay findable and readable; the adjustment form, which records a
   * *new* movement, narrows the same list to the active ones itself.
   */
  machines: LocalMachine[] = [];
  history: StockHistoryPage | null = null;
  loading = false;
  error = '';

  readonly StockAdjustmentReason = StockAdjustmentReason;
  readonly StockAdjustmentSource = StockAdjustmentSource;

  readonly pageSizeOptions = [25, 50, 100, 200];

  filters = {
    productId: null as number | null,
    from: '' as string,
    to: '' as string,
    reason: null as StockAdjustmentReason | null,
    machineId: null as number | null,
    source: null as StockAdjustmentSource | null,
    pageSize: 50
  };

  /** The page currently being shown; the server echoes back the page it actually served. */
  currentPage = 1;

  reasonOptions = [
    { value: StockAdjustmentReason.Restock, label: 'Restock' },
    { value: StockAdjustmentReason.Sale, label: 'Sale' },
    { value: StockAdjustmentReason.Damaged, label: 'Damaged' },
    { value: StockAdjustmentReason.Expired, label: 'Expired' },
    { value: StockAdjustmentReason.Correction, label: 'Correction' },
    { value: StockAdjustmentReason.MachineRefill, label: 'Machine refill' }
  ];

  private readonly allSourceOptions = [
    { value: StockAdjustmentSource.Manual, label: 'Manual' },
    { value: StockAdjustmentSource.Nayax, label: 'Nayax Sync Restock' }
  ];

  /** Set when the machine list could not be read at all; never shown as "no machines". */
  machinesError = '';

  /** Whether that read is still outstanding, which is not the same as there being no machines. */
  machinesLoading = false;

  /** Whether Nayax is part of this business at all; only the source filter depends on it. */
  providerRecordsAvailable = true;

  constructor(
    private route: ActivatedRoute,
    private stockService: StockService,
    private productService: ProductService,
    private localMachineService: LocalMachineService,
    private provider: ProviderAvailabilityService
  ) {}

  ngOnInit(): void {
    this.filters.productId = this.preselectedProductId();
    this.productService.getAll().subscribe({
      next: (products) => (this.products = products || []),
      error: () => (this.products = [])
    });
    this.loadMachines();
    this.provider
      .featureOffered('providerRecords')
      .subscribe((available) => (this.providerRecordsAvailable = available));
    this.load();
  }

  /** The source filter offers the Nayax source only while Nayax is part of this business. */
  get sourceOptions(): { value: StockAdjustmentSource; label: string }[] {
    return this.providerRecordsAvailable
      ? this.allSourceOptions
      : this.allSourceOptions.filter((option) => option.value !== StockAdjustmentSource.Nayax);
  }

  /**
   * Whether the machine filter is offered at all: only once the business has an **active** machine
   * to filter by (issue #746). A failed read is reported in its place instead
   * ({@link machinesError}) and an outstanding one says it is loading, so an empty filter is never
   * a statement about a business whose machines nobody read.
   */
  get machineFilterShown(): boolean {
    return this.machinesError === '' && !this.machinesLoading && this.machines.some((machine) => machine.isActive);
  }

  private loadMachines(): void {
    this.machinesLoading = true;
    this.machinesError = '';
    this.localMachineService.getAll().subscribe({
      next: (machines) => {
        this.machines = machines ?? [];
        this.machinesLoading = false;
      },
      error: () => {
        this.machines = [];
        this.machinesError = 'The machine list could not be loaded, so machines cannot be filtered.';
        this.machinesLoading = false;
      }
    });
  }

  /**
   * The product to preselect, from `/stock-history?productId=123` or from the preserved
   * `/products/123/stock` entry point. Anything unusable is ignored, which leaves the
   * all-products view rather than an empty history for a product that cannot exist.
   */
  private preselectedProductId(): number | null {
    const raw = this.route.snapshot.queryParamMap.get('productId') ?? this.route.snapshot.paramMap.get('id');
    if (raw === null || raw.trim() === '') return null;
    const parsed = Number(raw);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
  }

  load(): void {
    this.loading = true;
    this.error = '';
    this.stockService
      .historyPage({
        productId: this.filters.productId,
        from: this.filters.from || null,
        to: this.filters.to || null,
        reason: this.filters.reason,
        machineId: this.filters.machineId,
        source: this.filters.source,
        page: this.currentPage,
        pageSize: this.filters.pageSize
      })
      .subscribe({
        next: (page) => {
          this.history = page;
          this.currentPage = page.page;
          this.filters.pageSize = page.pageSize;
          this.loading = false;
        },
        error: (err) => {
          this.history = null;
          const body = err?.error;
          this.error =
            typeof body === 'string' ? body : (body?.message ?? body?.title ?? 'Failed to load stock history.');
          this.loading = false;
        }
      });
  }

  /** Any filter change restarts at the first page, so a narrowed filter cannot land past its end. */
  applyFilters(): void {
    this.currentPage = 1;
    this.load();
  }

  clearFilters(): void {
    this.filters = {
      productId: null,
      from: '',
      to: '',
      reason: null,
      machineId: null,
      source: null,
      pageSize: this.filters.pageSize
    };
    this.applyFilters();
  }

  get hasFilters(): boolean {
    return (
      this.filters.productId !== null ||
      this.filters.from !== '' ||
      this.filters.to !== '' ||
      this.filters.reason !== null ||
      this.filters.machineId !== null ||
      this.filters.source !== null
    );
  }

  get selectedProduct(): Product | null {
    if (this.filters.productId === null) return null;
    return this.products.find((product) => product.id === this.filters.productId) ?? null;
  }

  get totalPages(): number {
    if (!this.history || this.history.totalCount === 0) return 1;
    return Math.ceil(this.history.totalCount / this.history.pageSize);
  }

  nextPage(): void {
    if (!this.history?.hasMore) return;
    this.currentPage = this.history.page + 1;
    this.load();
  }

  previousPage(): void {
    if (this.currentPage <= 1) return;
    this.currentPage = this.currentPage - 1;
    this.load();
  }

  onAdjustmentApplied(): void {
    // The adjustment is already recorded; the history and the product's stock are re-read from the
    // API rather than patched locally, so what the operator sees is what was persisted.
    this.productService.getAll().subscribe({
      next: (products) => (this.products = products || []),
      error: () => undefined
    });
    this.currentPage = 1;
    this.load();
  }

  reasonLabel(reason: StockAdjustmentReason): string {
    return this.reasonOptions.find((option) => option.value === reason)?.label ?? 'Machine refill';
  }

  sourceLabel(source: StockAdjustmentSource): string {
    return source === StockAdjustmentSource.Nayax ? 'Nayax Sync Restock' : 'Manual';
  }

  /**
   * A movement's machine, named from the business's own records - deactivated machines included, so
   * history stays readable (issue #746). A machine the list does not hold is shown as its id rather
   * than as no machine at all.
   */
  machineLabel(machineId?: number | null): string {
    if (machineId === null || machineId === undefined) return '—';
    return this.machines.find((machine) => machine.id === machineId)?.name ?? String(machineId);
  }

  /** Unit cost is unavailable on many movements; an unknown cost is shown as unknown, never as $0.00. */
  unitCostLabel(unitCost?: number | null): string {
    return unitCost === null || unitCost === undefined ? '—' : `$${unitCost.toFixed(2)}`;
  }
}
