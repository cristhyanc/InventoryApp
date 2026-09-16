import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { StockService } from '../../services/stock.service';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { Product, StockAdjustment, StockAdjustmentReason, Machine, RestockCostSuggestion } from '../../models/models';

@Component({
  selector: 'app-stock-history',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './stock-history.component.html'
})
export class StockHistoryComponent implements OnInit {
  product: Product | null = null;
  history: StockAdjustment[] = [];
  machines: Machine[] = [];
  StockAdjustmentReason = StockAdjustmentReason;
  error = '';
  restockCostSuggestion: RestockCostSuggestion | null = null;
  private restockCostRequired = false;

  form = {
    quantityChange: null as number | null,
    reason: StockAdjustmentReason.Restock,
    machineId: null as number | null,
    notes: '',
    EatBefore: null as string | null,
    unitCost: null as number | null
  };

  reasonOptions = [
    { value: StockAdjustmentReason.Restock, label: 'Restock' },
    { value: StockAdjustmentReason.Sale, label: 'Sale' },
    { value: StockAdjustmentReason.Damaged, label: 'Damaged' },
    { value: StockAdjustmentReason.Expired, label: 'Expired' },
    { value: StockAdjustmentReason.Correction, label: 'Correction' },
    { value: StockAdjustmentReason.MachineRefill, label: 'Machine refill' }
  ];

  private productId!: number;

  constructor(
    private route: ActivatedRoute,
    private stockService: StockService,
    private productService: ProductService,
    private machineService: MachineService
  ) {}

  ngOnInit(): void {
    this.productId = Number(this.route.snapshot.paramMap.get('id'));
    this.load();
  }

  load(): void {
    this.productService.get(this.productId).subscribe((p) => (this.product = p));
    this.stockService.history(this.productId).subscribe((h) => (this.history = h));
    this.machineService.getAll().subscribe((ms) => (this.machines = ms || []));
  }

  reasonLabel(reason: StockAdjustmentReason): string {
    return this.reasonOptions.find((r) => r.value === reason)?.label ?? 'Machine refill';
  }

  getMachineLabel(machineId?: number | null): string {
    if (machineId === null || machineId === undefined) return '—';
    const m = this.machines.find((x) => x.machineID === machineId);
    return m?.machineName ?? String(machineId);
  }

  get requiresRestockCost(): boolean {
    return this.form.reason === StockAdjustmentReason.Restock && (this.form.quantityChange ?? 0) > 0;
  }

  get isCorrection(): boolean {
    return this.form.reason === StockAdjustmentReason.Correction;
  }

  get restockCostHelp(): string {
    if (this.restockCostSuggestion?.source === 'LastPurchase' && this.restockCostSuggestion.unitCost !== null) {
      return `Last purchase cost: $${this.restockCostSuggestion.unitCost.toFixed(2)}`;
    }
    if (this.restockCostSuggestion?.source === 'AverageUnitCost' && this.restockCostSuggestion.unitCost !== null) {
      return `Using current average cost: $${this.restockCostSuggestion.unitCost.toFixed(2)} - verify before saving`;
    }
    return 'No previous purchase cost is available. Enter the actual unit cost.';
  }

  onAdjustmentInputsChanged(): void {
    if (this.isCorrection && this.form.quantityChange !== null) {
      this.form.quantityChange = Math.abs(this.form.quantityChange);
    }

    if (this.requiresRestockCost && !this.restockCostRequired) {
      this.restockCostRequired = true;
      this.stockService.restockCostSuggestion(this.productId).subscribe({
        next: (suggestion) => {
          this.restockCostSuggestion = suggestion;
          this.form.unitCost = suggestion.unitCost;
        },
        error: () => {
          this.restockCostSuggestion = null;
          this.form.unitCost = null;
        }
      });
      return;
    }

    if (!this.requiresRestockCost) {
      this.restockCostRequired = false;
      this.restockCostSuggestion = null;
      this.form.unitCost = null;
    }
  }

  adjust(): void {
    const quantityChange = this.isCorrection
      ? -(Math.abs(this.form.quantityChange ?? 0))
      : this.form.quantityChange;
    if (quantityChange === null || quantityChange === 0) {
      this.error = this.isCorrection
        ? 'Enter a positive quantity to remove.'
        : 'Enter a non-zero quantity (use negative numbers to remove stock).';
      return;
    }
    const unitCost = this.form.unitCost;
    if (this.requiresRestockCost) {
      if (unitCost === null || unitCost === undefined) {
        this.error = 'Unit cost is required for a positive Restock adjustment.';
        return;
      }
      if (unitCost < 0) {
        this.error = 'Unit cost cannot be negative for a positive Restock adjustment.';
        return;
      }
    }
    this.error = '';

    const payload = {
      ...this.form,
      quantityChange,
      unitCost: this.requiresRestockCost ? this.form.unitCost : null
    };
    this.stockService.adjust(this.productId, payload).subscribe({
      next: () => {
        this.form = { quantityChange: null, reason: StockAdjustmentReason.Restock, machineId: null, notes: '', EatBefore: '', unitCost: null };
        this.restockCostRequired = false;
        this.restockCostSuggestion = null;
        this.load();
      },
      error: (err) => {
        this.error = err?.error ?? 'Failed to adjust stock.';
      }
    });
  }
}
