import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, switchMap, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { Machine, Product } from '../../models/models';

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

  constructor(
    private route: ActivatedRoute,
    private machineService: MachineService
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

        return this.machineService.getProducts(machineId).pipe(
          catchError(() => {
            this.error$.next('Failed to load products for this machine.');
            return of([] as Product[]);
          })
        );
      })
    );
  }
}
