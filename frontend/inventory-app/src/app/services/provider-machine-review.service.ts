import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * What the operator asked the review to do with one provider machine no machine of theirs carries
 * the id of. The numbers are the contract the API deserializes, so they must stay in step with the
 * backend `Inventory.Domain.Machines.ProviderMachineLinkAction` (issue #742).
 *
 * `Defer` is the honest default: a provider machine nobody decided about is left alone, never linked
 * by omission.
 */
export enum ProviderMachineLinkAction {
  Defer = 1,
  LinkExisting = 2,
  CreateNew = 3
}

/**
 * One machine of the business's sales provider, as the review reports it
 * (`Inventory.Application.Machines.ProviderMachineEntry`).
 *
 * `name` is what the provider calls the machine, or `Machine {providerMachineId}` when the provider
 * names it nothing - the name a machine created for it would carry. `localMachineId` is the machine
 * of this business carrying its provider id, or `null` when none does.
 */
export interface ProviderMachineEntry {
  providerMachineId: number;
  name: string;
  providerMachineNumber: string | null;
  localMachineId: number | null;
  localMachineName: string | null;
  isLinked: boolean;
}

/** One link that was recorded. `created` says whether the local machine is new. */
export interface ProviderMachineLinked {
  providerMachineId: number;
  localMachineId: number;
  name: string;
  created: boolean;
}

/**
 * A reading of the provider fleet against the business's own machines
 * (`ProviderMachineReview`, issue #742).
 *
 * `autoLinked` is what the read linked by itself, which it does only for a business with no machine
 * of its own lacking a provider id. `awaitingReview` is what it deliberately did not link, because
 * any of those may be a machine the operator already recorded by hand.
 */
export interface ProviderMachineReview {
  reviewedAtUtc: string;
  autoLinked: ProviderMachineLinked[];
  awaitingReview: ProviderMachineEntry[];
  linked: ProviderMachineEntry[];
  awaitingReviewCount: number;
  linkedCount: number;
}

/** What the operator decided about one provider machine. It carries no provider name or number. */
export interface ProviderMachineLinkDecision {
  providerMachineId: number;
  action: ProviderMachineLinkAction;
  localMachineId?: number | null;
}

/** The apply request: decisions and nothing else. */
export interface ApplyProviderMachineLinksRequest {
  decisions: ProviderMachineLinkDecision[];
}

/** What one apply recorded. One refused decision refuses all of them. */
export interface ProviderMachineLinksApplied {
  appliedAtUtc: string;
  links: ProviderMachineLinked[];
  createdCount: number;
  linkedCount: number;
}

/**
 * The provider machine review API (issue #742, parent #329 child G3): `/api/machine-link-review`.
 *
 * Deliberately a third machine resource beside {@link LocalMachineService} (the business's own
 * records, which need no provider) and {@link MachineService} (the live fleet). It is the join
 * between them, and the only surface that records a provider's machine id on a local machine.
 *
 * Both calls are `POST`, including the one that reads: reviewing the fleet links new provider
 * machines by itself for a business with nothing of its own unlinked, so it must never be served
 * from a cache. `Inventory.Operate` is the capability behind both.
 *
 * The service decides nothing. Every provider name and id comes from the API, and the apply carries
 * decisions alone - no provider value is ever sent back from the browser.
 */
@Injectable({ providedIn: 'root' })
export class ProviderMachineReviewService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/machine-link-review`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /** Reads the provider's fleet and reports it against this business's own machines. */
  review(): Observable<ProviderMachineReview> {
    return this.http.post<ProviderMachineReview>(this.baseUrl, {});
  }

  /** Records exactly the submitted decisions, as one transaction. */
  apply(request: ApplyProviderMachineLinksRequest): Observable<ProviderMachineLinksApplied> {
    return this.http.post<ProviderMachineLinksApplied>(`${this.baseUrl}/apply`, request);
  }
}
