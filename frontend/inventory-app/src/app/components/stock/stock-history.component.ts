import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { StockService } from '../../services/stock.service';
import { ProductService } from '../../services/product.service';
import { Product, StockAdjustment, StockAdjustmentReason } from '../../models/models';

@Component({
  selector: 'app-stock-history',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './stock-history.component.html'
})
export class StockHistoryComponent implements OnInit {
  product: Product | null = null;
  history: StockAdjustment[] = [];
  StockAdjustmentReason = StockAdjustmentReason;
  error = '';

  form = {
    quantityChange: 0,
    reason: StockAdjustmentReason.Restock,
    machineId: null as number | null,
    notes: '',
    EatBefore: null as string | null
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
    private productService: ProductService
  ) {}

  ngOnInit(): void {
    this.productId = Number(this.route.snapshot.paramMap.get('id'));
    this.load();
  }

  load(): void {
    this.productService.get(this.productId).subscribe((p) => (this.product = p));
    this.stockService.history(this.productId).subscribe((h) => (this.history = h));
  }

  reasonLabel(reason: StockAdjustmentReason): string {
    return this.reasonOptions.find((r) => r.value === reason)?.label ?? 'Machine refill';
  }

  adjust(): void {
    if (!this.form.quantityChange) {
      this.error = 'Enter a non-zero quantity (use negative numbers to remove stock).';
      return;
    }
    this.error = '';

    this.stockService.adjust(this.productId, this.form).subscribe({
      next: () => {
        this.form = { quantityChange: 0, reason: StockAdjustmentReason.Restock, machineId: null, notes: '', EatBefore: '' };
        this.load();
      },
      error: (err) => {
        this.error = err?.error ?? 'Failed to adjust stock.';
      }
    });
  }
}
