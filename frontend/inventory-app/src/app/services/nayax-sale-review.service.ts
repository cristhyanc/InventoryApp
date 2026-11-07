import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * A queue-delivered sale's verification state (issue #1104, extended by issue #1105). The API sends
 * the enum's numeric value.
 */
export enum NayaxSqsVerification {
  Unverified = 10,
  Verified = 20,
  Conflict = 30,
  NeedsInvestigation = 40,
  OwnerConfirmed = 50,
  OwnerCorrected = 60,
  OwnerExcluded = 70
}

/** The kind of evidence a row records (issue #1104). */
export enum NayaxSaleEvidenceKind {
  Comparison = 10,
  Absence = 20
}

/** One compared field's answer (issue #1104). */
export enum NayaxSaleFieldComparison {
  NotCompared = 10,
  Match = 20,
  Mismatch = 30
}

/** The four decisions a business Owner may make (issue #1105). */
export enum NayaxSaleOwnerDecision {
  UseNayaxValues = 10,
  KeepSqsValues = 20,
  DoNotCount = 30,
  Genuine = 40
}

export interface NayaxSaleReviewEvidence {
  id: number;
  kind: NayaxSaleEvidenceKind;
  observedAtUtc: string;
  resultingState: NayaxSqsVerification;
  providerMachineId: number | null;
  providerSettlementValue: number | null;
  providerAuthorizationInstantUtc: string | null;
  providerCurrencyCode: string | null;
  providerNayaxProductId: number | null;
  providerProductName: string | null;
  machineComparison: NayaxSaleFieldComparison | null;
  amountComparison: NayaxSaleFieldComparison | null;
  instantComparison: NayaxSaleFieldComparison | null;
  productComparison: NayaxSaleFieldComparison | null;
  readMachineId: number | null;
  returnedItemCount: number | null;
  returnedEarliestInstantUtc: string | null;
  returnedLatestInstantUtc: string | null;
}

export interface NayaxSaleReviewHistoryEntry {
  id: number;
  superseded: boolean;
  decision: NayaxSaleOwnerDecision | null;
  evidenceId: number;
  reason: string | null;
  occurredAtUtc: string;
  previousState: NayaxSqsVerification;
  resultingState: NayaxSqsVerification;
  reconciledPeriodFrom: string | null;
  reconciledPeriodTo: string | null;
  reconciledPeriodDifference: number | null;
}

export interface NayaxSaleAvailableDecision {
  decision: NayaxSaleOwnerDecision;
  evidenceId: number;
}

export interface NayaxSaleReviewItem {
  saleId: number;
  transactionId: number;
  machineId: number;
  machineName: string | null;
  settlementValue: number;
  authorizationInstantUtc: string;
  receivedViaSqsAtUtc: string | null;
  state: NayaxSqsVerification;
  isCounted: boolean;
  nayaxProductId: number | null;
  productId: number | null;
  productName: string | null;
  latestEvidenceId: number | null;
  latestComparison: NayaxSaleReviewEvidence | null;
  latestAbsence: NayaxSaleReviewEvidence | null;
  history: NayaxSaleReviewHistoryEntry[];
  availableDecisions: NayaxSaleAvailableDecision[];
}

export interface NayaxSaleReviewSummary {
  needsReviewCount: number;
  notCountedCount: number;
}

export interface NayaxSaleDecisionBody {
  presentedState: NayaxSqsVerification;
  presentedLatestEvidenceId: number;
  evidenceId: number;
  decision: NayaxSaleOwnerDecision;
  reason: string | null;
}

export interface NayaxSaleDecisionApplied {
  saleId: number;
  resultingState: NayaxSqsVerification;
  auditId: number;
}

/** The Nayax sales review API (issue #1105). */
@Injectable({ providedIn: 'root' })
export class NayaxSaleReviewService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/nayax-sale-review`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  list(): Observable<NayaxSaleReviewItem[]> {
    return this.http.get<NayaxSaleReviewItem[]>(this.baseUrl);
  }

  summary(): Observable<NayaxSaleReviewSummary> {
    return this.http.get<NayaxSaleReviewSummary>(`${this.baseUrl}/summary`);
  }

  decide(saleId: number, body: NayaxSaleDecisionBody): Observable<NayaxSaleDecisionApplied> {
    return this.http.post<NayaxSaleDecisionApplied>(`${this.baseUrl}/${saleId}/decision`, body);
  }
}

/** What a state is called on screen. Owner states are never shown as Nayax-verified. */
export function verificationLabel(state: NayaxSqsVerification): string {
  switch (state) {
    case NayaxSqsVerification.Unverified:
      return 'Not yet checked';
    case NayaxSqsVerification.Verified:
      return 'Verified by Nayax';
    case NayaxSqsVerification.Conflict:
      return 'Contradicted by Nayax - not counted';
    case NayaxSqsVerification.NeedsInvestigation:
      return 'Needs review - still counted';
    case NayaxSqsVerification.OwnerConfirmed:
    case NayaxSqsVerification.OwnerCorrected:
      return 'Confirmed by owner';
    case NayaxSqsVerification.OwnerExcluded:
      return 'Not counted - excluded by owner';
    default:
      return 'Unknown';
  }
}

/** What a decision is called on screen. */
export function decisionLabel(decision: NayaxSaleOwnerDecision): string {
  switch (decision) {
    case NayaxSaleOwnerDecision.UseNayaxValues:
      return "Use Nayax's values";
    case NayaxSaleOwnerDecision.KeepSqsValues:
      return 'Keep the SQS values';
    case NayaxSaleOwnerDecision.DoNotCount:
      return "Don't count this sale";
    case NayaxSaleOwnerDecision.Genuine:
      return 'This sale is genuine';
    default:
      return 'Decision';
  }
}

/** Whether a decision needs a typed reason (every one but "Use Nayax's values"). */
export function decisionRequiresReason(decision: NayaxSaleOwnerDecision): boolean {
  return decision !== NayaxSaleOwnerDecision.UseNayaxValues;
}
