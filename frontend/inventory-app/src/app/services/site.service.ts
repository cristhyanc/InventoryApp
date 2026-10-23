import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';
import { Site, SiteListItem, SiteProduct } from '../models/models';

@Injectable({ providedIn: 'root' })
export class SiteService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/sites`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  /**
   * Every site's full dashboard row, including its stock state and revenue. The backend builds it
   * from the machine list **and** every machine's products, so it costs one Nayax read per machine
   * in the fleet: ask for it only where those figures are shown - the Dashboard and the Sites list.
   * A site picker wants {@link getList} instead.
   */
  getAll(): Observable<Site[]> {
    return this.http.get<Site[]>(this.baseUrl);
  }

  /**
   * The light site list (issue #841): one site id, name and machine count per site, from one
   * machine-list read and no per-machine read at all - the count is the size of the site's group in
   * that same read. It answers the same Nayax unavailability states `getAll()` does, so
   * a caller still distinguishes "this business has no site filter" from "this business has no
   * sites" through `loadProviderList`.
   */
  getList(): Observable<SiteListItem[]> {
    return this.http.get<SiteListItem[]>(`${this.baseUrl}/list`);
  }

  getProducts(siteId: number): Observable<SiteProduct[]> {
    return this.http.get<SiteProduct[]>(`${this.baseUrl}/${siteId}/products`);
  }
}
