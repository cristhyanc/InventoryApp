import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { PlanFeature, SubscriptionPlanName, planRequiredOfError, requiredPlanOf } from '../../../access/plan-feature';
import { PlanLock, planLockOf } from '../../../access/plan-lock';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** The feature the Nayax sales file import is gated on - `ImportsController`'s `[RequirePlan]`. */
const SALES_FILE_IMPORT_FEATURE: PlanFeature = 'SalesFileImport';

/** What the inline lock's one sentence is about, named the same way the Plan page names it. */
const SALES_FILE_IMPORT_DESTINATION = 'Importing a Nayax sales file';

/**
 * The inline plan lock's own sentence (issue #979), naming the plan the business was actually told
 * it needs: `planLockSummary`'s sentence for {@link SALES_FILE_IMPORT_DESTINATION}, with the plan
 * taken from a `403 plan_required` refusal where there is one, rather than always from the declared
 * table - a refusal is the backend's own answer and names the plan it decided against.
 */
export function salesFileImportPlanLockedMessage(requiredPlan: SubscriptionPlanName): string {
  return `${SALES_FILE_IMPORT_DESTINATION} needs the ${requiredPlan} plan.`;
}

/**
 * Dedicated page for the supported import workflows, split out of `AdminComponent`
 * (issue #389, Admin split 2/3): the Nayax sales file upload and its CSV template, and the pending
 * reimbursement XML import. `ImportService` remains the sole authority for both calls, so no
 * import, deduplication, transaction-parsing or reimbursement rule is reimplemented or
 * reinterpreted here.
 *
 * <p><strong>Importing the product catalogue happens on its own page</strong> (issue #888):
 * `/products/import` (`ProductImportPageComponent`) hosts the catalogue review of issue #739, so
 * this page's Import products card is a link to it and renders no review of its own. It stopped
 * reading `?review=catalogue` (issue #875) with it, which that issue's own entry point - All
 * Products' Import products button - now points straight at the new page instead.</p>
 *
 * <p><strong>The Nayax sales card carries an inline plan lock</strong> (issue #979), and only that
 * card: this is a mixed page, so the XML import, the product-catalogue link and the client-side
 * template download are not plan-locked and the page itself is never route-locked. The lock is the
 * inline shape of issue #979 rather than the route-level one for exactly that reason, and it is
 * decided from the entitlement `GET /api/me/access` already carries (issue #966), through the same
 * `planLockOf` every other plan lock reads - so this component holds no plan rule of its own.
 * `ImportsController`'s `[RequirePlan(PlanFeature.SalesFileImport)]` (issue #967) stays the
 * authority: the lock withholds the submission controls so no refusable request is sent, and a
 * refusal that arrives anyway - a downgrade between reads, or an entitlement that could not be read
 * at all - is rendered in the card too rather than as a generic toast with a futile Retry behind
 * it.</p>
 *
 * <p>Role and support session are checked ahead of the plan and are unchanged: the route's
 * `requireCapability('Imports.Run')` is what decides whether this page exists for a member at all,
 * and {@link sessionActive} still disables every import control during a support session. A plan
 * lock therefore never appears instead of a role refusal, and never replaces the support notice.</p>
 */
@Component({
  selector: 'app-admin-imports',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Imports</h1>
          <p class="page-subtitle">Supported imports. These actions can change application data.</p>
        </div>
      </header>

      <div class="grid gap-5 md:grid-cols-2">
        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import Nayax sales</h2>
            <p class="mt-1 text-sm value-muted">Import .xlsx, .xls, or .csv sales data.</p>
            @if (salesImportPlanLocked) {
              <!--
                The inline lock (issue #979): one sentence naming the plan, a link to the Plan page
                (issue #970) and no Retry, because a plan refusal is this business's own answer and
                not a transient failure. A status role rather than an alert one for the same reason,
                and so it is announced when it replaces the controls after a refusal.
              -->
              <div class="mt-3 flex flex-wrap items-center gap-3" role="status" data-testid="sales-import-plan-lock">
                <p class="text-md-body value-muted">{{ salesImportPlanLockedMessage }}</p>
                <a routerLink="/plan" class="btn-link">View the Plan page</a>
              </div>
            }
            <div class="mt-4 flex flex-wrap gap-2">
              <button type="button" class="btn btn-secondary" (click)="downloadTemplate()">Download template</button>
              @if (!salesImportPlanLocked) {
                <!-- The file input lives beside the button that opens it, so neither is in the DOM while the card is locked. -->
                <input #salesFile type="file" accept=".xlsx,.xls,.csv" class="hidden" (change)="onSalesSelected($event)" />
                <button type="button" class="btn btn-primary" [disabled]="loading || sessionActive" (click)="salesFile.click()">{{ loading ? 'Importing...' : 'Choose sales file' }}</button>
              }
            </div>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import products</h2>
            <p class="mt-1 text-sm value-muted">
              Review the Nayax catalogue against your own products and categories, then link, create
              or update them per entry. Nothing is written until you apply the review.
            </p>
            <!--
              A link to the review's own page (issue #888) rather than a button that opened it here.
              It is not withheld or disabled during a support session: the destination renders the
              support notice instead of the review, so the import cannot be started either way.
            -->
            <a routerLink="/products/import" class="btn btn-primary mt-4">Import products</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import XML files</h2>
            <p class="mt-1 text-sm value-muted">Process pending reimbursement XML files.</p>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading || sessionActive" (click)="importXml()">Import XML files</button>
          </div>
        </section>
      </div>
    </div>
  `
})
export class AdminImportsComponent implements OnDestroy {
  loading = false;

  /**
   * Whether a support session is active (issue #661): both imports this page runs are disabled
   * while it is, because a session grants `ImportsRun` - the capability this page and its endpoints
   * are gated on - and the backend refuses the write itself only by its read-only method rule,
   * never by withholding the capability. See `SupportSessionService.active$`. The Import products
   * link is not one of them: it starts nothing, and `/products/import` applies the same rule itself
   * (issue #888).
   */
  sessionActive = false;

  /**
   * What the business's plan does to this one card, as `planLockOf` reads the entitlement. It starts
   * `open` and stays open while nothing has been read, the read failed, or an older API reported no
   * entitlement at all - the fail-open rule the whole plan lock is built on, which leaves the
   * backend's own refusal to answer for the business rather than withholding an import from one
   * that may perfectly well have bought it.
   */
  salesImportLock: PlanLock = 'open';

  /**
   * The plan a `403 plan_required` refusal named, once one has refused this import, and `null` while
   * none has. It is what makes the card handle a downgrade between reads, and an entitlement that
   * could not be read, inline: the entitlement said `open`, the request was made, and the backend
   * answered - so the card shows the plan it was actually told about instead of a generic error.
   */
  salesImportRefusedPlan: SubscriptionPlanName | null = null;

  private readonly sessionActiveSubscription: Subscription;
  private readonly entitlementSubscription: Subscription;

  constructor(
    private readonly importService: ImportService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService,
    access: AccessService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.entitlementSubscription = access.access$.subscribe((state) => {
      const entitlement = state.status === 'granted' ? state.access.entitlement : undefined;
      this.salesImportLock = planLockOf(entitlement, SALES_FILE_IMPORT_FEATURE);
      if (this.salesImportLock === 'open' && this.salesImportRefusedPlan !== null) {
        // The entitlement now includes the import, so a refusal recorded under the previous answer
        // is spent: an upgrade made elsewhere opens the card again rather than leaving it locked by
        // the last thing the backend said about a plan the business is no longer on.
        this.salesImportRefusedPlan = null;
      }
    });
  }

  /** Whether the card withholds its submission controls: the entitlement, or a refusal, says so. */
  get salesImportPlanLocked(): boolean {
    return this.salesImportLock === 'locked' || this.salesImportRefusedPlan !== null;
  }

  /** The locked card's one sentence, naming the refusal's plan where there is one. */
  get salesImportPlanLockedMessage(): string {
    return salesFileImportPlanLockedMessage(this.salesImportRefusedPlan ?? requiredPlanOf(SALES_FILE_IMPORT_FEATURE));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
    this.entitlementSubscription.unsubscribe();
  }

  onSalesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    // Checked here as well as disabled in the template, so the import is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    // And the same for the plan (issue #979), checked after the support session for the same reason
    // the template withholds the controls: a request the plan can only have refused is not sent.
    if (this.salesImportPlanLocked) return;
    if (!/\.(xlsx|xls|csv)$/i.test(file.name)) {
      this.toast.error('Only .xlsx, .xls, or .csv files are supported.');
      return;
    }
    this.loading = true;
    this.importService.importNayaxSales(file).subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.imported} sales, updated ${result.updated}, skipped ${result.skipped}.`); },
      error: err => {
        this.loading = false;
        // A `403 plan_required` is rendered in the card, never as a toast (issue #979): it is this
        // business's own answer about its plan, so it belongs where the import it refused would
        // have happened, and a toast would leave the controls offering an import the backend has
        // already refused. Every other failure keeps the message it always had.
        const requiredPlan = planRequiredOfError(err);
        if (requiredPlan !== null) {
          this.salesImportRefusedPlan = requiredPlan;
          return;
        }
        this.toast.error(err?.error?.message ?? err?.error ?? 'Failed to import Nayax sales.');
      }
    });
  }

  downloadTemplate(): void {
    const headers = ['TransactionID', 'TransactionStatusId', 'MachineID', 'NayaxProductId', 'MachineName', 'SettlementValue', 'PaymentMethod', 'ProductName', 'Product Cost Price', 'MachineAuthorizationTime'];
    const sample = ['1001', '12', '42', '987654', 'Machine A', '12.50', 'Card', 'Coke Zero', '1.100000', '2026-09-02 14:30:00'];
    const url = URL.createObjectURL(new Blob([[headers.join(','), sample.join(',')].join('\n')], { type: 'text/csv;charset=utf-8;' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'nayax-sales-import-template.csv'; anchor.click(); URL.revokeObjectURL(url);
  }

  importXml(): void {
    if (this.sessionActive) return;
    this.loading = true;
    this.importService.importPendingXmlFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
  }
}
