import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  MultiMachinePickListRefillRequest,
  MultiMachinePickListRefillResponse,
  PickListRefillRequest,
  PickListRefillResponse,
  PickListResult
} from '../models/models';
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

  /** Records a whole pick as one atomic, idempotent refill (issue #743). */
  recordRefill(request: PickListRefillRequest): Observable<PickListRefillResponse> {
    return this.http.post<PickListRefillResponse>(`${this.baseUrl}/refills`, request);
  }

  /**
   * Records a whole pick run across several machines as one atomic, idempotent batch (issue #897):
   * one `MachineRefill` movement per allocation, attributed to that allocation's own local machine.
   */
  recordMultiMachineRefill(request: MultiMachinePickListRefillRequest): Observable<MultiMachinePickListRefillResponse> {
    return this.http.post<MultiMachinePickListRefillResponse>(`${this.baseUrl}/multi-machine-refills`, request);
  }
}
