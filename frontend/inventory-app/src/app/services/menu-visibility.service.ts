import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One switchable destination as `GET /api/super-admin/menu-visibility` reports it (issues #1148 and
 * #1153).
 */
export interface MenuVisibilityOption {
  /** The server-owned catalogue key. It is never invented here: a write sends one back unchanged. */
  readonly key: string;
  /**
   * The menu group the destination sits in - `Reports` or `Admin` (issue #1153) - which the page
   * renders its sections from, so neither the grouping nor its wording is decided here.
   */
  readonly group: string;
  /** The destination's own display label, so this page needs no second copy of the menu's wording. */
  readonly label: string;
  /** Whether it is offered to members who are not the platform administrator, as now stored. */
  readonly visible: boolean;
}

/** The whole catalogue, grouped and in the server's own order. */
export interface MenuVisibilityCatalogue {
  readonly options: readonly MenuVisibilityOption[];
}

/**
 * The frontend client for the Super Admin menu-visibility surface (issues #1148 and #1151): the
 * catalogue read, and the one write that switches a single destination.
 *
 * It is a thin wrapper over those two endpoints and decides nothing. The **keys, the groups and the
 * labels are the server's**, listed as it reports them, so a catalogue the server extends needs no
 * change here;
 * who may make either call is the API's answer, and a caller who is not the configured platform
 * administrator is refused there whatever the UI offered. The per-key write is what keeps one
 * destination's failure away from the others: each row submits its own key and nothing else.
 */
@Injectable({ providedIn: 'root' })
export class MenuVisibilityService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** Every switchable destination with its label and the state it is in now, stored or defaulted. */
  list(): Observable<MenuVisibilityCatalogue> {
    return this.http.get<MenuVisibilityCatalogue>(this.url);
  }

  /**
   * Switches one destination on or off for everybody but a Super Admin, and answers that destination
   * as the server stored it. An undeclared key is refused with `404` and stores nothing.
   */
  set(key: string, visible: boolean): Observable<MenuVisibilityOption> {
    return this.http.put<MenuVisibilityOption>(`${this.url}/${encodeURIComponent(key)}`, { visible });
  }

  private get url(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/menu-visibility`;
  }
}
