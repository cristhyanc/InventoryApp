import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { LocalMachine } from '../models/models';
import { ConfigService } from './config.service';

/**
 * The business's own machine records (issue #740, parent #329 child G1): `/api/local-machines`.
 *
 * Deliberately separate from {@link MachineService}, which reads the live Nayax fleet at
 * `/api/machines` and needs working credentials for every call. Nothing here does: a business with
 * no sales provider creates, renames, deactivates and reactivates its machines through this
 * resource exactly as one with a provider does.
 *
 * The provider's own machine id is on neither side of this contract. It is written by the
 * `AddMachines` backfill and, later, by provider synchronization and the provider machine review
 * (issue #742); what a client is told is only whether the provider names the machine.
 */
@Injectable({ providedIn: 'root' })
export class LocalMachineService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/local-machines`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /** Every machine this business has recorded, active and inactive alike, ordered by name. */
  getAll(): Observable<LocalMachine[]> {
    return this.http.get<LocalMachine[]>(this.baseUrl);
  }

  create(name: string): Observable<LocalMachine> {
    return this.http.post<LocalMachine>(this.baseUrl, { name });
  }

  rename(id: number, name: string): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, { name });
  }

  deactivate(id: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/deactivate`, {});
  }

  reactivate(id: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/reactivate`, {});
  }
}
