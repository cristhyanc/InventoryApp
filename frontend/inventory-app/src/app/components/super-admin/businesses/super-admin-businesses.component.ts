import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subject, takeUntil } from 'rxjs';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { SuperAdminBusiness, SuperAdminBusinessesService } from '../../../services/super-admin-businesses.service';
import type { NayaxConnectionStatusName } from '../../../services/nayax-connection.service';
import { BusinessMembersDialogComponent } from './business-members-dialog.component';
import { BusinessRejectDialogComponent } from './business-reject-dialog.component';
import { ViewAsBusinessDialogComponent } from './view-as-business-dialog.component';

/** The filterable statuses, matching `Inventory.Domain.Tenancy.BusinessStatus`'s published names. */
export const SUPER_ADMIN_BUSINESS_STATUS_FILTERS = ['All', 'Pending', 'Active', 'Rejected', 'Deactivated'] as const;
export type SuperAdminBusinessStatusFilter = (typeof SUPER_ADMIN_BUSINESS_STATUS_FILTERS)[number];

/**
 * How each stored Nayax connection status is shown beside a business's name (issue #983): a dot
 * colour, and the sentence that is its tooltip and its screen-reader label.
 *
 * Green is the one verified state. Both states that mean "credentials are stored and are not known
 * to work" are orange, because neither is a working connection and neither is nothing at all - and
 * each keeps its **own sentence**, so the colour they share never makes "saved, never tested" and
 * "the last test failed" look like the same thing. Red is the business that has not started.
 *
 * The sentence is always rendered, never only hovered: colour alone is not a signal somebody who
 * cannot distinguish these three hues, or who is reading with a screen reader, can use.
 */
const NAYAX_STATUS_PRESENTATION: Record<NayaxConnectionStatusName, { dotClass: string; label: string }> = {
  Ready: { dotClass: 'bg-rr-ok-text', label: 'Nayax verified at last test' },
  PendingPermissions: { dotClass: 'bg-rr-warn-text', label: 'Nayax token saved, not tested yet' },
  NeedsAttention: { dotClass: 'bg-rr-warn-text', label: 'Nayax last test failed' },
  NotConfigured: { dotClass: 'bg-rr-danger-text', label: 'Nayax not set up' }
};

/**
 * What a status name this code does not declare is shown as. The API publishes the stored value as
 * it is, so an undeclared one is reported as unknown rather than presented as one of the four - the
 * same restraint the members list shows about a role nobody granted.
 */
const UNKNOWN_NAYAX_STATUS = { dotClass: 'bg-rr-line-strong', label: 'Nayax status unknown' } as const;

type PendingBusinessAction =
  | { readonly kind: 'approve'; readonly business: SuperAdminBusiness }
  | { readonly kind: 'deactivate'; readonly business: SuperAdminBusiness }
  | { readonly kind: 'reactivate'; readonly business: SuperAdminBusiness };

/**
 * The Super Admin Businesses page (issue #547), at `/super-admin/businesses`, over the eight
 * endpoints of the Super Admin businesses API (issues #544, #545). It is presentation only: the
 * API decides every rule a decision or transition might refuse, including the "no active Owner"
 * reactivation refusal, and this page shows that refusal's own sentence rather than a second copy
 * of the rule.
 *
 * Per docs/architecture.md § Page composition boundary, the page itself owns the table, the
 * status filter and the three single-call confirmations (approve, deactivate, reactivate) behind
 * the shared `ConfirmationDialogComponent` - the same shape `ProductListComponent` already uses
 * for a single-call confirmation - while Reject (its own reason text and submission state) and
 * Members (its own load, release confirmation and error) are dedicated feature components
 * composed through `[businessId]`/`[businessName]` and their outputs.
 *
 * The Nayax dot beside each business name (issue #983) is presentation only in the same sense: the
 * status it colours is the one the API already stored from that business's own last permission test,
 * this page neither tests nor re-tests anything, and loading the list makes no Nayax call.
 *
 * "View as this business" (issue #553, a slice of #513) is the one dedicated feature component
 * composed the same way Reject and Members are: its own reason text, submission state and error,
 * reached through `[businessId]`/`[businessName]` and reacting only to `(started)`/`(canceled)`.
 */
@Component({
  selector: 'app-super-admin-businesses',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    DatePipe,
    ConfirmationDialogComponent,
    BusinessRejectDialogComponent,
    BusinessMembersDialogComponent,
    ViewAsBusinessDialogComponent
  ],
  templateUrl: './super-admin-businesses.component.html'
})
export class SuperAdminBusinessesComponent implements OnInit, OnDestroy {
  readonly statusFilters = SUPER_ADMIN_BUSINESS_STATUS_FILTERS;

  businesses: SuperAdminBusiness[] = [];
  loading = false;
  loadError = false;
  statusFilter: SuperAdminBusinessStatusFilter = 'All';

  pendingAction: PendingBusinessAction | null = null;
  rejectTarget: SuperAdminBusiness | null = null;
  membersTarget: SuperAdminBusiness | null = null;
  viewAsTarget: SuperAdminBusiness | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly businessesService: SuperAdminBusinessesService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  get filteredBusinesses(): SuperAdminBusiness[] {
    return this.statusFilter === 'All'
      ? this.businesses
      : this.businesses.filter((business) => business.status === this.statusFilter);
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.businessesService
      .list()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (businesses) => {
          this.businesses = businesses;
          this.loading = false;
        },
        error: () => {
          this.loading = false;
          this.loadError = true;
        }
      });
  }

  /** The dot colour for one business's stored Nayax connection status (issue #983). */
  nayaxDotClass(business: SuperAdminBusiness): string {
    return this.nayaxPresentation(business).dotClass;
  }

  /** That status in words - the dot's tooltip and the label a screen reader reads (issue #983). */
  nayaxStatusLabel(business: SuperAdminBusiness): string {
    return this.nayaxPresentation(business).label;
  }

  requestApprove(business: SuperAdminBusiness): void {
    this.pendingAction = { kind: 'approve', business };
  }

  requestReject(business: SuperAdminBusiness): void {
    this.rejectTarget = business;
  }

  requestDeactivate(business: SuperAdminBusiness): void {
    this.pendingAction = { kind: 'deactivate', business };
  }

  requestReactivate(business: SuperAdminBusiness): void {
    this.pendingAction = { kind: 'reactivate', business };
  }

  openMembers(business: SuperAdminBusiness): void {
    this.membersTarget = business;
  }

  closeMembers(): void {
    this.membersTarget = null;
  }

  requestViewAs(business: SuperAdminBusiness): void {
    this.viewAsTarget = business;
  }

  onViewAsStarted(): void {
    // SupportSessionService.start already cleared the cached business data, re-read the
    // capabilities and navigated to the dashboard; this page is about to be torn down with it.
    this.viewAsTarget = null;
  }

  onViewAsCanceled(): void {
    this.viewAsTarget = null;
  }

  onMemberReleased(): void {
    this.load();
  }

  onRejected(): void {
    this.rejectTarget = null;
    this.toast.success('The application was rejected.');
    this.load();
  }

  onRejectCanceled(): void {
    this.rejectTarget = null;
  }

  cancelPendingAction(): void {
    this.pendingAction = null;
  }

  confirmPendingAction(): void {
    const action = this.pendingAction;
    if (!action) {
      return;
    }
    this.pendingAction = null;

    switch (action.kind) {
      case 'approve':
        this.businessesService.approve(action.business.businessId).subscribe({
          next: () => this.onActionSucceeded(`${action.business.name} was approved.`),
          error: (err) => this.onActionFailed(err, 'Unable to approve this application.')
        });
        break;
      case 'deactivate':
        this.businessesService.deactivate(action.business.businessId).subscribe({
          next: () => this.onActionSucceeded(`${action.business.name} was deactivated.`),
          error: (err) => this.onActionFailed(err, 'Unable to deactivate this business.')
        });
        break;
      case 'reactivate':
        this.businessesService.reactivate(action.business.businessId).subscribe({
          next: () => this.onActionSucceeded(`${action.business.name} was reactivated.`),
          error: (err) => this.onActionFailed(err, 'Unable to reactivate this business.')
        });
        break;
    }
  }

  get dialogTitle(): string {
    switch (this.pendingAction?.kind) {
      case 'approve':
        return 'Approve application';
      case 'deactivate':
        return 'Deactivate business';
      case 'reactivate':
        return 'Reactivate business';
      default:
        return '';
    }
  }

  get dialogMessage(): string {
    const action = this.pendingAction;
    if (!action) {
      return '';
    }
    const name = action.business.name;
    switch (action.kind) {
      case 'approve':
        return `Approve ${name}'s application? Its Owner will be able to trade immediately.`;
      case 'deactivate':
        return `Deactivate ${name}? Its members will lose access and its live invite links will be cancelled.`;
      case 'reactivate':
        return `Reactivate ${name}? Its active members will be able to reach it again.`;
      default:
        return '';
    }
  }

  private nayaxPresentation(business: SuperAdminBusiness): { dotClass: string; label: string } {
    return NAYAX_STATUS_PRESENTATION[business.nayaxConnectionStatus] ?? UNKNOWN_NAYAX_STATUS;
  }

  private onActionSucceeded(message: string): void {
    this.businessesService.notifyChanged();
    this.toast.success(message);
    this.load();
  }

  private onActionFailed(err: unknown, fallback: string): void {
    this.toast.error(extractErrorMessage(err, fallback));
    // A 409 or 404 means the business changed or disappeared elsewhere (another tab, another
    // Super Admin), so the row and the pending badge this page shows are stale: re-read both.
    if (isStaleStateStatus(err)) {
      this.businessesService.notifyChanged();
      this.load();
    }
  }
}

function isStaleStateStatus(err: unknown): boolean {
  const status = (err as { status?: unknown } | undefined)?.status;
  return status === 409 || status === 404;
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
