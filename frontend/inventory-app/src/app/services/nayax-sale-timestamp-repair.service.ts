import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The frontend client for the Nayax sale timestamp repair Preview/Apply maintenance operation
 * (`POST /api/admin/nayax-sale-timestamp-repair/preview` and `.../apply`, issue #472; the Admin UI
 * over it is issue #487).
 *
 * **The server owns every decision.** Nothing here parses or shifts a timestamp, converts a
 * business date, classifies an outcome, derives revenue movement or decides what may be repaired:
 * these types mirror `Inventory.Application.SaleTimestampRepair`'s contracts field for field so the
 * page can display the server's own figures unchanged. The two enum groups are numeric because the
 * API serializes enums with `System.Text.Json`'s defaults (`AddControllers()` registers no
 * `JsonStringEnumConverter`), so `outcome: 0` is `Repairable`.
 *
 * **The apply carries a preview id and an explicit confirmation, and nothing else.** There is
 * deliberately no way to express a client-supplied instant, business date, outcome, provenance or
 * row list through this client: the server reads all of them from the plan its own preview stored
 * (see docs/architecture.md § Nayax sale timestamp repair: Preview then Apply).
 */

/** Where an authoritative authorization instant came from. Mirrors `NayaxSaleTimestampEvidenceSource`. */
export enum NayaxSaleTimestampEvidenceSource {
  NayaxLastSalesApi = 1,
  OperatorExport = 2
}

/** What the server decided about one examined sale. Mirrors `NayaxSaleTimestampRepairOutcome`. */
export enum NayaxSaleTimestampRepairOutcome {
  Repairable = 0,
  AlreadyCorrect = 1,
  Unresolved = 2
}

/** Why a sale was left exactly as it is. Mirrors `NayaxSaleTimestampUnresolvedReason`. */
export enum NayaxSaleTimestampUnresolvedReason {
  NoSourceEvidence = 0,
  UnreadableEvidence = 1,
  ConflictingEvidence = 2,
  MachineMismatch = 3,
  AmountMismatch = 4
}

/**
 * One examined stored sale. `storedInstantUtc`/`repairedInstantUtc` are true UTC instants and must
 * be rendered with `BusinessDateTimePipe`; `storedBusinessDate`/`repairedBusinessDate` are already
 * `Australia/Sydney` calendar dates the server resolved and carry no timezone designator, so they
 * are rendered as plain dates and never re-converted.
 */
export interface NayaxSaleTimestampRepairRow {
  transactionId: number;
  machineId: number;
  machineName: string | null;
  settlementValue: number;
  transactionStatusId: number | null;
  completedSale: boolean;
  storedInstantUtc: string;
  storedBusinessDate: string;
  repairedInstantUtc: string | null;
  repairedBusinessDate: string | null;
  outcome: NayaxSaleTimestampRepairOutcome;
  unresolvedReason: NayaxSaleTimestampUnresolvedReason | null;
  evidenceSource: NayaxSaleTimestampEvidenceSource | null;
  evidenceReference: string | null;
}

/** Revenue a repair moves between Sydney business days. Completed sales only. */
export interface NayaxSaleTimestampRevenueMovement {
  businessDate: string;
  amountLeaving: number;
  amountArriving: number;
  netMovement: number;
}

/**
 * A product whose costing the apply would replay. `rebuildPlanned` is the server's own gate: a
 * product with no inventory-cost transition baseline, or whose replay would start at or before that
 * baseline's cutoff, is not replayed, so an affected product is not the same thing as a rebuilt one.
 */
export interface NayaxSaleTimestampRepairProduct {
  productId: number;
  productName: string;
  rebuildFromUtc: string;
  hasTransitionBaseline: boolean;
  transitionCutoffAt: string | null;
  rebuildPlanned: boolean;
}

/** One Sydney business day of the fixed-cutoff reconciliation. */
export interface NayaxSaleTimestampReconciliationDay {
  businessDate: string;
  completedCountBefore: number;
  completedSalesBefore: number;
  completedCountAfter: number;
  completedSalesAfter: number;
  unresolvedCount: number;
  unresolvedAmount: number;
  sourceVerifiedCountAfter: number;
  sourceVerifiedAfter: number;
}

/**
 * The fixed-cutoff reconciliation. `sourceVerifiedTotalAfter` is the figure comparable with the
 * source export, because it covers exactly the transactions a source accounted for;
 * `totalAfter` still carries the unresolved rows and is therefore never "fully reconciled" while
 * `unresolvedCompletedCount` or `missingFromDatabaseCount` is above zero.
 */
export interface NayaxSaleTimestampReconciliation {
  cutoffUtc: string;
  fromBusinessDate: string;
  toBusinessDate: string;
  days: NayaxSaleTimestampReconciliationDay[];
  completedCountBefore: number;
  totalBefore: number;
  completedCountAfter: number;
  totalAfter: number;
  sourceVerifiedCountAfter: number;
  sourceVerifiedTotalAfter: number;
  excludedAfterCutoffCount: number;
  excludedAfterCutoffAmount: number;
  unresolvedCompletedCount: number;
  unresolvedCompletedAmount: number;
  missingFromDatabaseCount: number;
  missingFromDatabaseAmount: number;
}

/**
 * A transaction the evidence carries that this business holds no stored sale for. A missing sale is
 * not a timestamp defect: a repair can never create one, and importing it is the separate ordinary
 * uploaded-export import.
 */
export interface NayaxSaleTimestampMissingSale {
  transactionId: number;
  machineId: number;
  authorizationInstantUtc: string | null;
  authorizationBusinessDate: string | null;
  settlementValue: number;
  transactionStatusId: number | null;
  completedSale: boolean;
  evidenceSource: NayaxSaleTimestampEvidenceSource;
}

/**
 * The stored plan the server computed and the identifier the apply must name back. `expiresAt` is
 * the server's own two-hour lifetime: the plan can be applied once and only before it.
 */
export interface NayaxSaleTimestampRepairPreview {
  previewId: string;
  evidenceRecords: number;
  examinedFromUtc: string | null;
  examinedToUtc: string | null;
  salesExamined: number;
  repairable: number;
  alreadyCorrect: number;
  unresolved: number;
  rows: NayaxSaleTimestampRepairRow[];
  revenueMovement: NayaxSaleTimestampRevenueMovement[];
  affectedProducts: NayaxSaleTimestampRepairProduct[];
  missingFromDatabase: NayaxSaleTimestampMissingSale[];
  reconciliation: NayaxSaleTimestampReconciliation | null;
  createdAt: string;
  expiresAt: string;
}

/** One applied repair, exactly as the append-only audit recorded it. */
export interface NayaxSaleTimestampRepairRecord {
  id: number;
  transactionId: number;
  machineId: number;
  previousInstantUtc: string;
  repairedInstantUtc: string;
  previousBusinessDate: string;
  repairedBusinessDate: string;
  evidenceSource: NayaxSaleTimestampEvidenceSource;
  evidenceReference: string;
  previewId: string;
  appliedAt: string;
  appliedByDirectoryTenantId: string;
  appliedByObjectId: string;
}

/** What the apply actually wrote, as the server counted it. */
export interface NayaxSaleTimestampRepairApplied {
  previewId: string;
  salesRepaired: number;
  productsRebuilt: number;
  recostedSales: number;
  repairs: NayaxSaleTimestampRepairRecord[];
}

/**
 * The fixed-cutoff reconciliation window to request. `cutoffUtc` is an explicitly labelled ISO UTC
 * instant (with its `Z`) and the two business dates are plain `yyyy-MM-dd` Sydney calendar dates;
 * all three are sent verbatim so no browser-local offset can shift the compared window.
 */
export interface NayaxSaleTimestampReconciliationWindow {
  cutoffUtc: string;
  fromBusinessDate: string;
  toBusinessDate: string;
}

/** Which sources a preview reads, and the optional window it reconciles. */
export interface NayaxSaleTimestampRepairPreviewRequest {
  includeLatestSalesApiEvidence: boolean;
  evidenceExport: File | null;
  reconciliation: NayaxSaleTimestampReconciliationWindow | null;
}

/**
 * The preview request cap, mirroring `NayaxSaleTimestampRepairsController.MaxEvidenceExportBytes`.
 * It is here so the page can refuse an upload the server would reject at the HTTP boundary anyway;
 * the server enforces it twice itself (`[RequestSizeLimit]` and `[RequestFormLimits]`) and is the
 * only authority. Raising this constant would weaken nothing and achieve nothing.
 */
export const EVIDENCE_EXPORT_MAX_BYTES = 8_000_000;

/**
 * The export formats the server's reader accepts, mirroring
 * `Inventory.Application.Imports.NayaxSalesExportFormats.Supported`. Client-side help only: the
 * server refuses an unsupported file independently of this list.
 */
export const EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS: readonly string[] = ['.xlsx', '.xls', '.csv'];

@Injectable({ providedIn: 'root' })
export class NayaxSaleTimestampRepairService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/admin/nayax-sale-timestamp-repair`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * Asks the server what repairing the named sources' evidence would do. It writes no sale
   * timestamp - only its own plan draft - and returns the `previewId` the apply requires back.
   *
   * The body is `FormData` and no request options are passed, so the browser sets the multipart
   * boundary itself; a hand-written `Content-Type` would produce a body the server's multipart
   * reader cannot parse. The three reconciliation fields are appended together or not at all,
   * because the server refuses a partial window.
   */
  preview(request: NayaxSaleTimestampRepairPreviewRequest): Observable<NayaxSaleTimestampRepairPreview> {
    const form = new FormData();
    form.append('includeLatestSalesApiEvidence', String(request.includeLatestSalesApiEvidence));
    if (request.reconciliation) {
      form.append('reconciliationCutoffUtc', request.reconciliation.cutoffUtc);
      form.append('reconciliationFromBusinessDate', request.reconciliation.fromBusinessDate);
      form.append('reconciliationToBusinessDate', request.reconciliation.toBusinessDate);
    }
    if (request.evidenceExport) {
      form.append('evidenceExport', request.evidenceExport, request.evidenceExport.name);
    }
    return this.http.post<NayaxSaleTimestampRepairPreview>(`${this.baseUrl}/preview`, form);
  }

  /** Confirms exactly the server's own stored plan. Nothing else is, or can be, submitted. */
  apply(previewId: string): Observable<NayaxSaleTimestampRepairApplied> {
    return this.http.post<NayaxSaleTimestampRepairApplied>(`${this.baseUrl}/apply`, {
      previewId,
      confirmed: true
    });
  }
}
