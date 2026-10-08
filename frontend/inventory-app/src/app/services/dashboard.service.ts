import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DashboardSummary } from '../models/models';
import { ConfigService } from './config.service';

/**
 * The home Dashboard's authoritative summary endpoint (issue #459), consumed by
 * `DashboardComponent` (issue #460). A thin HTTP boundary only: it adds no figure of its own.
 */
@Injectable({ providedIn: 'root' })
export class DashboardService {
  constructor(private http: HttpClient, private config: ConfigService) {}

  getSummary(): Observable<DashboardSummary> {
    return this.http.get<DashboardSummary>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/dashboard/summary`);
  }
}
