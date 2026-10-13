import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { Subject, filter, switchMap, take, takeUntil } from 'rxjs';
import { AccountStateService } from './account-state.service';
import { REDEEMABLE_ACCOUNT_STATES, nonRedeemableAccountStateDestination } from './account-state.guard';
import {
  BusinessApplicationDraft,
  BusinessApplicationErrors,
  BusinessApplicationField,
  MAX_BUSINESS_NAME_LENGTH,
  availableTimeZones,
  browserTimeZone,
  isValidContactEmail,
  toBusinessApplicationRequest,
  validateBusinessApplication
} from './business-application-rules';
import { BusinessApplicationService } from './business-application.service';

export const ONBOARDING_DISABLED_MESSAGE = "Starting a new business isn't available right now.";
export const RATE_LIMITED_MESSAGE =
  'You have submitted too many business applications recently. Wait a few minutes and try again.';
export const GENERIC_SUBMIT_ERROR_MESSAGE = "Your application couldn't be submitted. Try again.";

/** The claims a sign-in token may carry the person's address in, in the API's preference order. */
const EMAIL_CLAIMS = ['preferred_username', 'email'];

/** Reads the sentence an API refusal carries, falling back to one this page owns. */
function submitErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return GENERIC_SUBMIT_ERROR_MESSAGE;
  }

  // A disabled deployment answers with a bare 404 and no body at all, by design.
  if (error.status === 404) {
    return ONBOARDING_DISABLED_MESSAGE;
  }

  const body = error.error as { message?: unknown; detail?: unknown } | null;
  if (error.status === 429) {
    return typeof body?.detail === 'string' && body.detail.trim().length > 0 ? body.detail : RATE_LIMITED_MESSAGE;
  }

  if ((error.status === 400 || error.status === 409) && typeof body?.message === 'string' && body.message.trim().length > 0) {
    return body.message;
  }

  return GENERIC_SUBMIT_ERROR_MESSAGE;
}

/**
 * The business application form at `/apply` (issue #543, one slice of #508 under #328): where a
 * signed-in person with no active membership fills in their business's details and applies to start
 * it. The welcome page, the rejected page and the access-unavailable page link here when
 * `onboardingEnabled` is true.
 *
 * **Who may see it follows the API's eligibility rule.** It is offered to exactly the account
 * states `REDEEMABLE_ACCOUNT_STATES` names - `NoMembership`, `MembershipRevoked` and
 * `ApplicationRejected`, the ones with no active membership, which is
 * `MembershipEligibility.AllowsActiveMembership` on the backend - and only while `onboardingEnabled`
 * is true. Any other known answer is sent to the page its state already calls for. An account state
 * that could not be read shows the form anyway, for the fail-open reason `accountStateGuard`
 * documents: the endpoint re-checks eligibility itself and its answer is the one that counts.
 *
 * **Inline validation is the API's.** Every rule and sentence comes from
 * `business-application-rules.ts`, which mirrors `BusinessSettingsPolicy`; a field's message shows
 * once the field has been left or a submit was attempted. The contact address starts as the
 * signed-in account's own, read from the sign-in token's claims, and stays editable; the time zone
 * starts as the browser's and is chosen from the browser's IANA list.
 *
 * **Submitting lands on the pending page only once the account state says so.** A recorded
 * application re-reads `AccountStateService` before navigating to `/application-pending`, so the
 * pages the router guards see `ApplicationPending` rather than the state this person applied from.
 * A refusal is shown exactly as the API wrote it where it carries a sentence (`400`, `409`, and the
 * `429` detail); the bare `404` of a disabled deployment gets one sentence of this page's own.
 */
@Component({
  selector: 'app-business-application',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Start a new business</h1>
        </div>
      </header>

      @if (phase === 'form') {
        <section class="card">
          <div class="card-body">
            <p class="text-md-body">
              Tell us about your business. The Super Admin reviews every application before the business can be used.
            </p>

            @if (submitError) {
              <p class="mt-3 text-sm text-red-600" data-testid="submit-error">{{ submitError }}</p>
            }

            <form class="mt-4 flex flex-col gap-4" (ngSubmit)="submit()" novalidate>
              <div class="field">
                <label class="field-label" for="businessName">Business name</label>
                <input
                  id="businessName"
                  name="name"
                  [(ngModel)]="draft.name"
                  (blur)="touch('name')"
                  [attr.maxlength]="maxNameLength"
                  autocomplete="organization"
                />
                @if (shownError('name'); as message) {
                  <p class="mt-1 text-sm text-red-600" data-testid="error-name">{{ message }}</p>
                }
              </div>

              <div class="field">
                <label class="field-label" for="businessContactEmail">Contact email</label>
                <input
                  id="businessContactEmail"
                  name="contactEmail"
                  type="email"
                  [(ngModel)]="draft.contactEmail"
                  (blur)="touch('contactEmail')"
                  autocomplete="email"
                />
                @if (shownError('contactEmail'); as message) {
                  <p class="mt-1 text-sm text-red-600" data-testid="error-contactEmail">{{ message }}</p>
                }
              </div>

              <div class="field">
                <label class="field-label" for="businessAbn">ABN</label>
                <input
                  id="businessAbn"
                  name="abn"
                  inputmode="numeric"
                  [(ngModel)]="draft.abn"
                  (blur)="touch('abn')"
                  placeholder="Optional, e.g. 51 824 753 556"
                />
                @if (shownError('abn'); as message) {
                  <p class="mt-1 text-sm text-red-600" data-testid="error-abn">{{ message }}</p>
                }
              </div>

              <fieldset class="field">
                <legend class="field-label">Registered for GST</legend>
                <div class="flex items-center gap-4">
                  <label class="inline-flex items-center gap-2">
                    <input
                      type="radio"
                      name="gstRegistered"
                      [value]="true"
                      [(ngModel)]="draft.gstRegistered"
                      (change)="touch('gstRegistered')"
                      data-testid="gst-yes"
                    />
                    Yes
                  </label>
                  <label class="inline-flex items-center gap-2">
                    <input
                      type="radio"
                      name="gstRegistered"
                      [value]="false"
                      [(ngModel)]="draft.gstRegistered"
                      (change)="touch('gstRegistered')"
                      data-testid="gst-no"
                    />
                    No
                  </label>
                </div>
                @if (shownError('gstRegistered'); as message) {
                  <p class="mt-1 text-sm text-red-600" data-testid="error-gstRegistered">{{ message }}</p>
                }
              </fieldset>

              <div class="field">
                <label class="field-label" for="businessTimeZone">Time zone</label>
                <input
                  id="businessTimeZone"
                  name="timeZoneId"
                  list="business-time-zones"
                  [(ngModel)]="draft.timeZoneId"
                  (blur)="touch('timeZoneId')"
                  autocomplete="off"
                  placeholder="Search, e.g. Australia/Sydney"
                />
                <datalist id="business-time-zones">
                  @for (zone of timeZones; track zone) {
                    <option [value]="zone"></option>
                  }
                </datalist>
                @if (shownError('timeZoneId'); as message) {
                  <p class="mt-1 text-sm text-red-600" data-testid="error-timeZoneId">{{ message }}</p>
                }
              </div>

              <div class="page-actions">
                <button type="submit" class="btn btn-primary" [disabled]="submitting">
                  {{ submitting ? 'Submitting...' : 'Submit application' }}
                </button>
              </div>
            </form>
          </div>
        </section>
      }
    </div>
  `
})
export class BusinessApplicationComponent implements OnInit, OnDestroy {
  readonly maxNameLength = MAX_BUSINESS_NAME_LENGTH;
  readonly timeZones: readonly string[] = availableTimeZones();

  phase: 'checking' | 'form' = 'checking';
  submitting = false;
  submitError: string | null = null;

  draft: BusinessApplicationDraft = {
    name: '',
    contactEmail: '',
    abn: '',
    gstRegistered: null,
    timeZoneId: browserTimeZone() ?? ''
  };

  private readonly touched = new Set<BusinessApplicationField>();
  private submitAttempted = false;
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly accountState: AccountStateService,
    private readonly applications: BusinessApplicationService,
    private readonly authService: MsalService,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    this.draft.contactEmail = this.signedInEmail();

    this.accountState.state$
      .pipe(
        filter((answer) => answer.status !== 'unread'),
        take(1),
        takeUntil(this.destroying$)
      )
      .subscribe((answer) => {
        if (
          answer.status === 'known' &&
          (!answer.accountState.onboardingEnabled || !REDEEMABLE_ACCOUNT_STATES.has(answer.accountState.state))
        ) {
          this.router.navigateByUrl(nonRedeemableAccountStateDestination(answer.accountState.state), {
            replaceUrl: true
          });
          return;
        }

        this.phase = 'form';
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /** The current rule violations, recomputed from the draft so they always match what is typed. */
  get errors(): BusinessApplicationErrors {
    return validateBusinessApplication(this.draft);
  }

  /** The message to show under a field: only once it has been left, or a submit was attempted. */
  shownError(field: BusinessApplicationField): string | null {
    return this.submitAttempted || this.touched.has(field) ? (this.errors[field] ?? null) : null;
  }

  touch(field: BusinessApplicationField): void {
    this.touched.add(field);
  }

  submit(): void {
    if (this.submitting) {
      return;
    }

    this.submitAttempted = true;
    this.submitError = null;

    if (Object.keys(this.errors).length > 0) {
      return;
    }

    this.submitting = true;
    this.applications
      .submit(toBusinessApplicationRequest(this.draft))
      .pipe(
        switchMap(() => this.accountState.refresh()),
        takeUntil(this.destroying$)
      )
      .subscribe({
        next: () => this.router.navigateByUrl('/application-pending', { replaceUrl: true }),
        error: (error: unknown) => {
          this.submitting = false;
          this.submitError = submitErrorMessage(error);
        }
      });
  }

  /** The signed-in account's own address, when its sign-in token carries a usable one. */
  private signedInEmail(): string {
    const instance = this.authService.instance;
    const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0] ?? null;
    if (account === null) {
      return '';
    }

    const claims = (account.idTokenClaims ?? {}) as Record<string, unknown>;
    const candidates = [...EMAIL_CLAIMS.map((claim) => claims[claim]), account.username];
    const email = candidates.find(
      (value): value is string => typeof value === 'string' && isValidContactEmail(value)
    );
    return email?.trim() ?? '';
  }
}
