import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { GstClassification, Supplier, SupplierGstDefaults } from '../models/models';
import { ConfigService } from './config.service';

export interface SupplierDto {
  name: string;
  contactName?: string | null;
  phone?: string | null;
  email?: string | null;
  address?: string | null;
}

/**
 * The body of `PUT /api/suppliers/{id}/gst-defaults` (issue #430): all three defaults are replaced
 * together, so the caller sends the values it last read plus whatever it changed.
 */
export interface SupplierGstDefaultsDto {
  productLineGstDefault: GstClassification;
  deliveryGstDefault: GstClassification;
  packageGstDefault: GstClassification;
}

@Injectable({ providedIn: 'root' })
export class SupplierService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/suppliers`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(): Observable<Supplier[]> {
    return this.http.get<Supplier[]>(this.baseUrl);
  }

  get(id: number): Observable<Supplier> {
    return this.http.get<Supplier>(`${this.baseUrl}/${id}`);
  }

  create(payload: SupplierDto): Observable<Supplier> {
    return this.http.post<Supplier>(this.baseUrl, payload);
  }

  update(id: number, payload: SupplierDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  /** The supplier's explicitly configured GST defaults (issue #430); their own resource. */
  getGstDefaults(id: number): Observable<SupplierGstDefaults> {
    return this.http.get<SupplierGstDefaults>(`${this.baseUrl}/${id}/gst-defaults`);
  }

  setGstDefaults(id: number, payload: SupplierGstDefaultsDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}/gst-defaults`, payload);
  }
}
