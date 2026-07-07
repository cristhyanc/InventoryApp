import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { Receipt } from '../../models/models';

@Component({
  selector: 'app-receipt-list',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './receipt-list.component.html'
})
export class ReceiptListComponent implements OnInit {
  receipts: Receipt[] = [];

  constructor(private receiptService: ReceiptService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.receiptService.getAll().subscribe((r) => (this.receipts = r));
  }

  fileUrl(receipt: Receipt): string {
    return this.receiptService.fileUrl(receipt.id);
  }

  isImage(receipt: Receipt): boolean {
    return receipt.contentType.startsWith('image/');
  }

  remove(receipt: Receipt): void {
    if (!confirm(`Delete receipt "${receipt.title}"?`)) return;
    this.receiptService.delete(receipt.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
