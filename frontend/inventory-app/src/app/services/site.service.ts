import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';
import { Site, SiteProduct } from '../models/models';

@Injectable({ providedIn: 'root' })
export class SiteService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/sites`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(): Observable<Site[]> {
    return this.http.get<Site[]>(this.baseUrl);
  }

  getProducts(siteId: number): Observable<SiteProduct[]> {
    return this.http.get<SiteProduct[]>(`${this.baseUrl}/${siteId}/products`);
  }
}
