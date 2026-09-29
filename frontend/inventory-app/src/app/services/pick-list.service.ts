import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PickListResult } from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class PickListService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/pick-list`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  /** The read-only restock-planning projection (issue #221) for the given machines. */
  get(machineIds: number[]): Observable<PickListResult> {
    const params = machineIds.reduce((p, id) => p.append('machineIds', id), new HttpParams());
    return this.http.get<PickListResult>(this.baseUrl, { params });
  }
}
