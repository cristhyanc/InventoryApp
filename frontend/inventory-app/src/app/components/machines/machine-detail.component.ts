import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { Machine, Product } from '../../models/models';

@Component({
  selector: 'app-machine-detail',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './machine-detail.component.html'
})
export class MachineDetailComponent implements OnInit {
  machine: Machine | null = null;
  products: Product[] = [];
  loading = false;
  error = '';

  constructor(
    private route: ActivatedRoute,
    private machineService: MachineService
  ) {}

  ngOnInit(): void {
    const idParam = this.route.snapshot.paramMap.get('id');
    const machineId = idParam ? Number(idParam) : NaN;

    if (!machineId || isNaN(machineId)) {
      this.error = 'Invalid machine id.';
      return;
    }

    this.loading = true;
    this.machineService.get(machineId).subscribe({
      next: (machine) => {
        this.machine = machine;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load machine details.';
        this.loading = false;
      }
    });

    this.machineService.getProducts(machineId).subscribe({
      next: (products) => (this.products = products),
      error: () => {
        this.error = 'Failed to load products for this machine.';
      }
    });
  }
}
