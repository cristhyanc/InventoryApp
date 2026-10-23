import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One platform supplier preset as `GET /api/super-admin/supplier-presets` reports it (issue #848).
 * These six fields are the whole contract: the stored normalised name the server compares by is
 * deliberately not published, because whether two names collide is the database's answer and not a
 * decision any client makes.
 */
export interface SuperAdminSupplierPreset {
  readonly id: number;
  readonly name: string;
  readonly url: string | null;
  readonly isEnabled: boolean;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/** What a create submits. A new preset is created enabled, so there is no `isEnabled`. */
export interface CreateSupplierPresetRequest {
  readonly name: string;
  readonly url: string | null;
}

/** What an edit submits: the whole resource, so enabling and disabling is this call. */
export interface UpdateSupplierPresetRequest {
  readonly name: string;
  readonly url: string | null;
  readonly isEnabled: boolean;
}

/**
 * The frontend client for `/api/super-admin/supplier-presets` (issue #848), consumed by the Super
 * Admin Supplier presets page. It is a thin wrapper over the three endpoints: no name, URL or
 * duplicate rule is decided here, only what the server already returned.
 *
 * There is deliberately no `delete`: the API has none, and a preset that should stop being suggested
 * is disabled through {@link update}.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminSupplierPresetsService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** Every preset, enabled and disabled alike, ordered by name. No filter parameter exists. */
  list(): Observable<SuperAdminSupplierPreset[]> {
    return this.http.get<SuperAdminSupplierPreset[]>(this.baseUrl);
  }

  create(request: CreateSupplierPresetRequest): Observable<SuperAdminSupplierPreset> {
    return this.http.post<SuperAdminSupplierPreset>(this.baseUrl, request);
  }

  update(id: number, request: UpdateSupplierPresetRequest): Observable<SuperAdminSupplierPreset> {
    return this.http.put<SuperAdminSupplierPreset>(`${this.baseUrl}/${id}`, request);
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/supplier-presets`;
  }
}
