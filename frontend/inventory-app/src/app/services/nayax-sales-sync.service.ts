import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The explicit, shared latest-Nayax-sales synchronization call (issue #187). The home dashboard
 * calls this once before loading Sites and Machines so both sections read the same freshness
 * boundary, instead of Machines importing the latest transactions as a side effect of its own load.
 */
@Injectable({ providedIn: 'root' })
export class NayaxSalesSyncService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/nayax-sales-sync`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  syncLatest(): Observable<void> {
    return this.http.post<void>(this.baseUrl, {});
  }
}
