import { Component, ElementRef, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import {
  NayaxConnectionCheckLine,
  NayaxConnectionRetestResult,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName,
  NayaxConnectionTestResult
} from '../../../services/nayax-connection.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import {
  NayaxConnectionChecklistComponent,
  nayaxConnectionAreaLabel,
  nayaxConnectionFailureSentence
} from './nayax-connection-checklist.component';

/** One outcome of the last action an Owner took, with what to do about it. */
export interface NayaxConnectionNotice {
  readonly tone: 'danger' | 'warning' | 'success';
  readonly title: string;
  readonly detail: string;
}

/** Nayax's customer support portal, the first place to ask for a missing permission. */
export const NAYAX_SUPPORT_PORTAL_URL = 'https://nayax.service-now.com/csm';

/** The integration mailbox, as a secondary contact when the portal gets no answer. */
export const NAYAX_SUPPORT_EMAIL = 'integration-support@nayax.com';

/**
 * What a re-test refused for a changed credential means. The server sends this sentence itself, and
 * it is displayed as sent; this constant is what a `409` carrying no body still says, because
 * "something went wrong" would leave an Owner re-testing a token that is no longer stored.
 */
const CONNECTION_CHANGED_WHILE_TESTING = 'The connection changed while testing. Run the test again.';

/** The status names in an operator's language, with the badge each one earns. */
const STATUS_PRESENTATION: Record<NayaxConnectionStatusName, { label: string; badge: string }> = {
  NotConfigured: { label: 'Not connected', badge: 'badge-neutral' },
  Ready: { label: 'Ready', badge: 'badge-success' },
  PendingPermissions: { label: 'Permissions pending', badge: 'badge-warning' },
  NeedsAttention: { label: 'Needs attention', badge: 'badge-danger' }
};

/** The six steps, in order, for the progress list. */
const STEP_LABELS = [
  'Operator ID',
  'Token instructions',
  'Paste the token',
  'Checklist',
  'Ask Nayax',
  'Save'
];

/**
 * The Owner-only Nayax connection wizard (issue #538), composed by the Nayax Settings page beside
 * the processing-fee editor, over the endpoints of issues #536 and #537.
 *
 * **The token is write-only.** The field on step 3 is bound to no component state at all: it is
 * read from the DOM at the moment Test is pressed, passed once to `POST …/test`, and cleared the
 * moment that call returns, whatever it concluded. Nothing here writes it to `localStorage`,
 * `sessionStorage`, a cookie, the URL or a component field, and nothing re-sends it - the save step
 * carries the server's own opaque draft id, so the credentials stored are exactly the ones the
 * server tested (see docs/architecture.md § Saving a tested connection and re-testing it).
 *
 * **It decides nothing about the connection.** Every check line, cause, machine count and status on
 * screen is the server's own finding. There is no rule here that concludes a token works, no second
 * reading of a Nayax refusal, and no status this component derives: `NotChecked` is rendered as
 * neither a pass nor a failure, and a run whose sign-in never passed is not offered for saving
 * because the server refuses it - this only avoids spending a request on a refusal already known.
 *
 * **Owner only, twice over and authoritative in neither place.** The route already requires
 * `Integration.Manage` (`app.routes.ts`), and the endpoints behind every action require it
 * themselves and refuse the request regardless. The capability read here hides a control an Owner's
 * role is the only one that can use; it starts granted so an unanswered read leaves the wizard
 * exactly as it was before roles were rendered.
 *
 * **A support session is the other reason Test, Save connection and Re-test are disabled** (issue
 * #660): a session grants `IntegrationManage`, the very capability `canManage` reads above, so
 * `SupportSessionService.active$` is the flag that actually decides it - see
 * docs/architecture.md § Hiding write controls and support-view messages.
 */
@Component({
  selector: 'app-nayax-connection-wizard',
  standalone: true,
  imports: [CommonModule, FormsModule, NayaxConnectionChecklistComponent],
  template: `
    <section class="card" data-testid="nayax-connection">
      <div class="card-header">
        <h2 class="card-title">Nayax connection</h2>
      </div>
      <div class="card-body">
        @if (!canManage) {
          <p class="text-sm value-muted" data-testid="not-owner">
            Only an Owner may connect this business's Nayax account or re-test it.
          </p>
        } @else {
          @if (notice) {
            <div
              class="alert"
              role="status"
              [class.alert-danger]="notice.tone === 'danger'"
              [class.alert-warning]="notice.tone === 'warning'"
              [class.alert-success]="notice.tone === 'success'"
              data-testid="notice"
            >
              <p class="alert-title">{{ notice.title }}</p>
              <p class="mt-1">{{ notice.detail }}</p>
            </div>
          }

          @if (statusUnavailable) {
            <p class="text-sm value-muted" data-testid="status-unavailable">
              The current Nayax connection state could not be read. The steps below still work, and
              saving a tested token reports the state the server concluded.
            </p>
          }

          @if (connectionStored && connection) {
            <div class="mb-5 rounded-lg border border-md-gray-200 p-4" data-testid="stored-connection">
              <div class="flex flex-wrap items-center gap-2">
                <h3 class="text-sm font-semibold text-md-gray-800">Stored connection</h3>
                <span [class]="'badge ' + statusBadge(connection.status)">
                  {{ statusLabel(connection.status) }}
                </span>
              </div>
              @if (connection.machineChecksPending) {
                <p class="mt-2 text-sm value-muted" data-testid="machine-checks-pending">
                  Re-test once your machines are set up. The machine-scoped checks never ran, because
                  this Nayax account had no machine to run them against, so those permissions are
                  still unverified.
                </p>
              }
              <button
                type="button"
                class="btn btn-secondary mt-3"
                data-testid="retest"
                [disabled]="busy || sessionActive"
                (click)="retest()"
              >{{ retesting ? 'Re-testing…' : 'Re-test this connection' }}</button>

              @if (retestResult) {
                <div class="mt-4" data-testid="retest-checklist">
                  <app-nayax-connection-checklist
                    [checks]="retestResult.checks"
                    [machineCount]="retestResult.machineCount"
                  ></app-nayax-connection-checklist>
                </div>
              }
            </div>
          }

          <ol class="mb-4 flex flex-wrap gap-2 text-sm" data-testid="step-progress">
            @for (label of stepLabels; track label; let index = $index) {
              <li>
                <button
                  type="button"
                  class="btn btn-sm"
                  [class.btn-primary]="step === index + 1"
                  [class.btn-secondary]="step !== index + 1"
                  [attr.aria-current]="step === index + 1 ? 'step' : null"
                  [disabled]="busy || !canGoTo(index + 1)"
                  (click)="goTo(index + 1)"
                >{{ index + 1 }}. {{ label }}</button>
              </li>
            }
          </ol>

          @if (step === 1) {
            <div data-testid="step-1">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 1. Your Nayax operator id</h3>
              <div class="field mt-2 sm:max-w-xs">
                <label class="field-label" for="nayax-operator-id">Operator ID</label>
                <input
                  id="nayax-operator-id"
                  type="text"
                  inputmode="numeric"
                  autocomplete="off"
                  [disabled]="busy"
                  [(ngModel)]="operatorId"
                />
                <p class="field-hint">
                  In Nayax Core: <strong>Operator</strong> &rsaquo;
                  <strong>Company Info</strong> &rsaquo; <strong>Operator Id</strong>. Open Operator
                  in the left sidebar, select your operator and search; the numeric id is listed
                  under Company Info. It is not a secret, and it is stored with the connection.
                </p>
              </div>
            </div>
          }

          @if (step === 2) {
            <div data-testid="step-2">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 2. Create a Lynx token in Nayax Core</h3>
              <ol class="mt-2 ml-5 list-decimal text-sm text-md-gray-800" data-testid="token-instructions">
                <li>Sign in to Nayax Core with your username and password.</li>
                <li>Click your username in the top right corner, then choose Account Settings.</li>
                <li>Open the Security and Login tab, the second one, next to Account.</li>
                <li>Find the User Tokens section, below Your Trusted Devices.</li>
                <li>Choose Add Token, and select the Lynx Token type.</li>
                <li>
                  If a Lynx token is already listed, use Show Token next to it instead, and copy it.
                </li>
              </ol>
              <p class="mt-2 text-sm value-muted">
                The token is a secret. Paste it on the next step; it is sent once to check it and is
                not kept in this browser.
              </p>
            </div>
          }

          @if (step === 3) {
            <div data-testid="step-3">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 3. Paste the token and test it</h3>
              <div class="field mt-2 sm:max-w-md">
                <label class="field-label" for="nayax-token">Lynx token</label>
                <input
                  #tokenField
                  id="nayax-token"
                  type="password"
                  autocomplete="off"
                  spellcheck="false"
                  [disabled]="busy"
                />
                <p class="field-hint">
                  Write-only: it is sent once with the test, discarded by this page as soon as the
                  test comes back, and never stored in this browser. Nothing is saved yet &mdash;
                  testing changes no Nayax data and leaves any stored connection untouched.
                </p>
              </div>
              <button
                type="button"
                class="btn btn-primary mt-3"
                data-testid="run-test"
                [disabled]="busy || sessionActive"
                (click)="runTest()"
              >{{ testing ? 'Testing…' : 'Test connection' }}</button>
            </div>
          }

          @if (step === 4 && result) {
            <div data-testid="step-4">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 4. What this token can do</h3>
              <p class="mt-1 mb-3 text-sm value-muted">
                Every line is the server's own result for one Nayax endpoint this application reads.
                Nothing has been stored yet.
              </p>
              <app-nayax-connection-checklist
                [checks]="result.checks"
                [machineCount]="result.machineCount"
              ></app-nayax-connection-checklist>
            </div>
          }

          @if (step === 5 && result) {
            <div data-testid="step-5">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 5. Ask Nayax for the missing permissions</h3>
              <p class="mt-1 text-sm value-muted">
                Nayax grants a token's permissions, so this is a request to them. Copy the text below
                into a support ticket; it names your operator id and every area that was refused.
              </p>
              <label class="field-label mt-3" for="nayax-support-request">Request to send Nayax</label>
              <textarea
                #supportRequestField
                id="nayax-support-request"
                class="w-full rounded-lg border border-md-gray-200 p-3 font-mono text-xs"
                rows="12"
                readonly
                data-testid="support-request"
                [value]="supportRequest()"
              ></textarea>
              <div class="mt-2 flex flex-wrap gap-2">
                <button
                  type="button"
                  class="btn btn-secondary"
                  data-testid="copy-support-request"
                  (click)="copySupportRequest()"
                >Copy this request</button>
              </div>
              <p class="mt-3 text-sm text-md-gray-800">
                Raise it in the Nayax support portal:
                <a
                  class="btn-link"
                  data-testid="support-portal-link"
                  target="_blank"
                  rel="noopener noreferrer"
                  [href]="supportPortalUrl"
                >{{ supportPortalUrl }}</a>
              </p>
              <p class="mt-1 text-sm value-muted">
                If the portal gets no answer, the integration mailbox is the secondary contact:
                <a class="btn-link" data-testid="support-email-link" [href]="'mailto:' + supportEmail">
                  {{ supportEmail }}
                </a>
              </p>
            </div>
          }

          @if (step === 6) {
            <div data-testid="step-6">
              <h3 class="text-sm font-semibold text-md-gray-800">Step 6. Save this connection</h3>
              @if (result) {
                <p class="mt-1 text-sm value-muted">
                  Saving stores the operator id and the token the server just tested, and nothing
                  else: this page sends back only the server's own reference to that test.
                </p>
                @if (!signInPassed) {
                  <p class="mt-2 text-sm text-md-danger-text" data-testid="sign-in-failed">
                    This token did not sign in to Nayax, so it cannot be saved. Create or copy a
                    working Lynx token and test again from step 2.
                  </p>
                } @else if (draftExpired) {
                  <p class="mt-2 text-sm text-md-danger-text">
                    This test result has expired. Run the test again from step 3 before saving.
                  </p>
                } @else if (!result.nothingFailed) {
                  <p class="mt-2 text-sm text-md-gray-800">
                    Some areas were refused. Saving now records the connection as having permissions
                    pending, so the features that need those areas keep saying so until Nayax grants
                    them and a re-test confirms it.
                  </p>
                }
                <button
                  type="button"
                  class="btn btn-primary mt-3"
                  data-testid="save-connection"
                  [disabled]="!canSave"
                  (click)="saveConnection()"
                >{{ saving ? 'Saving…' : 'Save connection' }}</button>
              } @else if (saved) {
                <p class="mt-1 text-sm text-md-success-text" data-testid="saved">
                  Saved. This business now calls Nayax with the token you tested.
                </p>
                <button
                  type="button"
                  class="btn btn-secondary mt-3"
                  data-testid="connect-another"
                  [disabled]="busy"
                  (click)="startOver()"
                >Connect a different token</button>
              } @else {
                <p class="mt-1 text-sm value-muted">
                  There is no completed test to save. Run the test again from step 3.
                </p>
              }
            </div>
          }

          <div class="mt-4 flex flex-wrap gap-2">
            @if (step > 1) {
              <button
                type="button"
                class="btn btn-secondary"
                data-testid="back"
                [disabled]="busy"
                (click)="back()"
              >Back</button>
            }
            @if (step !== 3 && step !== 6) {
              <button
                type="button"
                class="btn btn-primary"
                data-testid="next"
                [disabled]="busy"
                (click)="next()"
              >Next</button>
            }
          </div>
        }
      </div>
    </section>
  `
})
export class NayaxConnectionWizardComponent implements OnInit, OnDestroy {
  readonly stepLabels = STEP_LABELS;
  readonly supportPortalUrl = NAYAX_SUPPORT_PORTAL_URL;
  readonly supportEmail = NAYAX_SUPPORT_EMAIL;

  /** Which step is on screen, 1 to 6. */
  step = 1;

  /** The operator id. Not a secret, and the only credential field this component holds. */
  operatorId = '';

  /**
   * Whether the signed-in member's role may manage this integration. It starts `true` so an
   * unanswered capability read leaves the wizard exactly as it was before roles were rendered; the
   * route guard and every endpoint check it themselves.
   */
  canManage = true;

  /**
   * Whether a support session is active (issue #660): Test, Save connection and Re-test are
   * disabled while it is, because a session grants `IntegrationManage` - the same capability
   * {@link canManage} reads - and the backend refuses the write itself only by its read-only
   * method rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  /** The stored connection's state, as the server reported it. */
  connection: NayaxConnectionState | null = null;

  /** True when the stored state could not be read; the wizard stays usable regardless. */
  statusUnavailable = false;

  /** The completed test awaiting a save. It carries the draft id and the check lines, never a token. */
  result: NayaxConnectionTestResult | null = null;

  /** The last re-test of the stored credentials. */
  retestResult: NayaxConnectionRetestResult | null = null;

  testing = false;
  saving = false;
  retesting = false;

  /** True once a draft has been promoted into the connection from this screen. */
  saved = false;

  notice: NayaxConnectionNotice | null = null;

  /**
   * The token field. It is read at the moment Test is pressed and cleared when that call returns:
   * there is deliberately no `ngModel`, signal or field holding the token between those two points.
   */
  @ViewChild('tokenField') private readonly tokenField?: ElementRef<HTMLInputElement>;

  @ViewChild('supportRequestField')
  private readonly supportRequestField?: ElementRef<HTMLTextAreaElement>;

  private statusSubscription: Subscription | null = null;
  private testSubscription: Subscription | null = null;
  private saveSubscription: Subscription | null = null;
  private retestSubscription: Subscription | null = null;
  private readonly manageAccess: Subscription;
  private readonly sessionActiveSubscription: Subscription;

  private testSequence = 0;

  constructor(
    private readonly connections: NayaxConnectionService,
    private readonly toast: ToastService,
    access: AccessService,
    supportSession: SupportSessionService
  ) {
    this.manageAccess = access
      .allows('Integration.Manage')
      .subscribe((allowed) => (this.canManage = allowed));
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.loadStatus();
  }

  ngOnDestroy(): void {
    // The field goes with the view, but clearing it is explicit rather than incidental.
    this.discardToken();
    this.manageAccess.unsubscribe();
    this.sessionActiveSubscription.unsubscribe();
    this.statusSubscription?.unsubscribe();
    this.testSubscription?.unsubscribe();
    this.saveSubscription?.unsubscribe();
    this.retestSubscription?.unsubscribe();
  }

  get busy(): boolean {
    return this.testing || this.saving || this.retesting;
  }

  /** Whether the business has credentials stored at all. */
  get connectionStored(): boolean {
    return this.connection !== null && this.connection.status !== 'NotConfigured';
  }

  /** Whether any line of the completed test failed, as the server's own flag reports it. */
  get anyFailed(): boolean {
    return this.result !== null && !this.result.nothingFailed;
  }

  /** Whether the tested token signed in at all. The server refuses to save one that did not. */
  get signInPassed(): boolean {
    return this.result?.checks.some((line) => line.check === 'SignIn' && line.status === 'Passed') ?? false;
  }

  /** Whether the server's short-lived draft has already stopped existing. */
  get draftExpired(): boolean {
    return this.result !== null && Date.parse(this.result.expiresAtUtc) <= Date.now();
  }

  get canSave(): boolean {
    return (
      this.canManage &&
      !this.sessionActive &&
      !this.busy &&
      this.result !== null &&
      this.signInPassed &&
      !this.draftExpired
    );
  }

  statusLabel(status: NayaxConnectionStatusName): string {
    return STATUS_PRESENTATION[status]?.label ?? status;
  }

  statusBadge(status: NayaxConnectionStatusName): string {
    return STATUS_PRESENTATION[status]?.badge ?? 'badge-neutral';
  }

  /** Whether a step can be opened at all: the result-bearing steps need a completed test. */
  canGoTo(step: number): boolean {
    if (step < 1 || step > STEP_LABELS.length) {
      return false;
    }
    if (step >= 4) {
      return this.result !== null || (step === 6 && this.saved);
    }
    return true;
  }

  goTo(step: number): void {
    if (!this.canGoTo(step)) {
      return;
    }
    this.step = step;
  }

  next(): void {
    if (this.step === 1) {
      if (!this.operatorId.trim()) {
        this.notice = {
          tone: 'danger',
          title: 'Enter the operator id from Nayax Core first.',
          detail:
            'Nothing was sent to Nayax. The operator id is in Nayax Core under Operator › ' +
            'Company Info › Operator Id.'
        };
        return;
      }
      this.notice = null;
      this.goTo(2);
      return;
    }
    if (this.step === 4) {
      this.goTo(this.anyFailed ? 5 : 6);
      return;
    }
    this.goTo(this.step + 1);
  }

  back(): void {
    this.goTo(this.previousStep());
  }

  /** Starts a fresh connection attempt, keeping nothing of the completed one. */
  startOver(): void {
    this.result = null;
    this.retestResult = null;
    this.saved = false;
    this.notice = null;
    this.discardToken();
    this.step = 1;
  }

  /**
   * Runs the server's checks with the operator id on screen and the token in the field.
   *
   * The token is read here, passed once, and cleared the moment the call returns - on a success, a
   * refusal and a transport failure alike.
   */
  runTest(): void {
    if (!this.canManage || this.sessionActive || this.busy) {
      return;
    }

    const operatorId = this.operatorId.trim();
    if (!operatorId) {
      this.notice = {
        tone: 'danger',
        title: 'Enter the operator id from Nayax Core first.',
        detail: 'Nothing was sent to Nayax. Go back to step 1 and enter the operator id.'
      };
      this.goTo(1);
      return;
    }

    const token = this.tokenField?.nativeElement.value.trim() ?? '';
    if (!token) {
      this.notice = {
        tone: 'danger',
        title: 'Paste the Lynx token before testing.',
        detail: 'Nothing was sent to Nayax. Step 2 has the path to the token in Nayax Core.'
      };
      return;
    }

    this.testSubscription?.unsubscribe();
    const sequence = ++this.testSequence;
    this.testing = true;
    this.notice = null;
    this.result = null;
    this.retestResult = null;
    this.saved = false;

    this.testSubscription = this.connections.test(operatorId, token).subscribe({
      next: (result) => {
        this.discardToken();
        if (sequence !== this.testSequence) return;
        this.testing = false;
        this.result = result;
        this.step = 4;
        if (result.nothingFailed) {
          this.toast.success('Every check this token was asked for passed.');
        } else {
          this.toast.warning('Some Nayax areas were refused. Step 5 has the request to send Nayax.');
        }
      },
      error: (error: unknown) => {
        this.discardToken();
        if (sequence !== this.testSequence) return;
        this.testing = false;
        this.notice = this.describeFailure(error, 'The connection test could not be completed.', 'test');
      }
    });
  }

  /** Promotes the completed test into this business's connection, by draft id alone. */
  saveConnection(): void {
    const result = this.result;
    if (!this.canManage || this.sessionActive || this.busy || !result) {
      return;
    }
    if (!this.signInPassed) {
      this.notice = {
        tone: 'danger',
        title: 'The tested token did not sign in to Nayax, so it was not saved.',
        detail:
          'A token that has never been shown to authenticate must not replace what this business ' +
          'is using. Create or copy a working Lynx token and test again.'
      };
      return;
    }
    if (this.draftExpired) {
      this.result = null;
      this.notice = {
        tone: 'danger',
        title: 'This test result has expired.',
        detail: 'The server keeps a tested token for fifteen minutes only. Run the test again, then save.'
      };
      return;
    }

    this.saveSubscription?.unsubscribe();
    this.saving = true;
    this.notice = null;

    this.saveSubscription = this.connections.save(result.draftId).subscribe({
      next: (connection) => {
        this.saving = false;
        this.connection = connection;
        this.statusUnavailable = false;
        this.saved = true;
        // The draft is consumed: withdrawing it is what makes a second save impossible here as
        // well as refused by the server.
        this.result = null;
        this.retestResult = null;
        this.step = 6;
        this.toast.success('Nayax connection saved.');
      },
      error: (error: unknown) => {
        this.saving = false;
        // A draft is single-use, so this one can never be saved again whatever refused it. A fresh
        // test is the only safe next step, and the stored state is re-read rather than assumed.
        this.result = null;
        this.notice = this.describeFailure(error, 'The connection could not be saved.', 'save');
        this.loadStatus();
      }
    });
  }

  /** Runs the same checks again with the token this business already has stored. */
  retest(): void {
    if (!this.canManage || this.sessionActive || this.busy) {
      return;
    }

    this.retestSubscription?.unsubscribe();
    this.retesting = true;
    this.notice = null;
    this.retestResult = null;

    this.retestSubscription = this.connections.retest().subscribe({
      next: (result) => {
        this.retesting = false;
        this.retestResult = result;
        this.connection = result.connection;
        this.statusUnavailable = false;
        if (!result.statusUpdated) {
          this.notice = {
            tone: 'warning',
            title: 'Nayax never answered, so nothing was concluded.',
            detail:
              'The stored status is left exactly as it was - nothing about the credentials was ' +
              'learned. Try the re-test again in a few minutes.'
          };
          return;
        }
        this.notice = result.nothingFailed
          ? {
              tone: 'success',
              title: 'Every check the re-test ran passed.',
              detail: 'The stored status now reflects this run.'
            }
          : {
              tone: 'warning',
              title: 'Some Nayax areas were refused.',
              detail:
                'The stored status now reflects this run. Ask Nayax for the areas the checklist ' +
                'marks with a cross, then re-test.'
            };
      },
      error: (error: unknown) => {
        this.retesting = false;
        const fallback =
          this.errorCode(error) === 'nayax_not_connected'
            ? 'This business has no stored Nayax credentials to re-test.'
            : CONNECTION_CHANGED_WHILE_TESTING;
        this.notice = this.describeFailure(error, fallback, 'retest');
        this.loadStatus();
      }
    });
  }

  /**
   * The request to send Nayax: the operator id and every area that was refused, with the cause the
   * server reported for each. It names no token, and it is built from the one area/cause vocabulary
   * the checklist uses, so the screen and the ticket cannot say different things.
   */
  supportRequest(): string {
    const operatorId = this.operatorId.trim() || '(operator id not entered)';
    const refused = this.failedChecks().map(
      (line) => `- ${nayaxConnectionAreaLabel(line.check)}: ${nayaxConnectionFailureSentence(line.failure)}`
    );

    return [
      'Subject: Lynx API token permissions for our inventory integration',
      '',
      `Operator ID: ${operatorId}`,
      'Token type: Lynx Token (a user token created in Nayax Core under Account Settings › ' +
        'Security and Login › User Tokens)',
      '',
      'We tested this token against the Lynx API endpoints our inventory application reads. These',
      'areas were refused:',
      ...refused,
      '',
      'Please grant this token read access to the endpoints listed above for this operator id, or',
      'tell us which permission covers them so we can request it.',
      '',
      'We have not included the token itself in this request.'
    ].join('\n');
  }

  /** Puts the request on the clipboard, and leaves it selectable when there is no clipboard. */
  copySupportRequest(): void {
    const request = this.supportRequest();
    const clipboard = navigator.clipboard;
    if (!clipboard?.writeText) {
      this.selectSupportRequest();
      this.toast.info('Select the request text and copy it with your keyboard.');
      return;
    }

    clipboard.writeText(request).then(
      () => this.toast.success('Request copied. Paste it into a Nayax support ticket.'),
      () => {
        this.selectSupportRequest();
        this.toast.info('Select the request text and copy it with your keyboard.');
      }
    );
  }

  /** The lines that were refused. A `NotChecked` line is not one: nothing was asked of it. */
  private failedChecks(): NayaxConnectionCheckLine[] {
    return (this.result?.checks ?? []).filter(
      (line) => line.status !== 'Passed' && line.status !== 'NotChecked'
    );
  }

  private previousStep(): number {
    if (this.step === 6) {
      if (this.result === null) {
        return 3;
      }
      return this.anyFailed ? 5 : 4;
    }
    if (this.step === 5 || this.step === 4) {
      return this.step === 5 ? 4 : 3;
    }
    return Math.max(1, this.step - 1);
  }

  private loadStatus(): void {
    this.statusSubscription?.unsubscribe();
    this.statusSubscription = this.connections.status().subscribe({
      next: (connection) => {
        this.connection = connection;
        this.statusUnavailable = false;
      },
      error: () => (this.statusUnavailable = true)
    });
  }

  private discardToken(): void {
    const field = this.tokenField?.nativeElement;
    if (field) {
      field.value = '';
    }
  }

  private selectSupportRequest(): void {
    this.supportRequestField?.nativeElement.select();
  }

  /**
   * What a refused request means, with the server's own caller-safe sentence whenever it sent one.
   * `fallback` is what the screen says when a refusal carries no body at all.
   */
  private describeFailure(
    error: unknown,
    fallback: string,
    action: 'test' | 'save' | 'retest'
  ): NayaxConnectionNotice {
    const status = this.statusOf(error);
    const message = this.serverMessage(error);

    if (status === 429) {
      return {
        tone: 'warning',
        title: message ?? 'This business has run too many Nayax connection tests recently.',
        detail:
          'Test and re-test share one allowance for the whole business. No Nayax call was made ' +
          'and nothing was stored. Wait a few minutes and try again.'
      };
    }
    if (status === 401 || status === 403) {
      return {
        tone: 'danger',
        title: 'This request was not authorized, so nothing was changed.',
        detail:
          'Only an Owner may manage the Nayax connection. Sign in again, then try once the session ' +
          'is current.'
      };
    }
    if (status === 0 || status === 408 || status === 502 || status === 503 || status === 504) {
      return {
        tone: 'danger',
        title: 'The request did not reach the server, or did not come back.',
        detail:
          action === 'save'
            ? 'Whether the connection was stored is not known from here, so the stored state above ' +
              'was re-read. Check it before testing again.'
            : 'Nothing was concluded about your Nayax credentials. Check the connection and try again.'
      };
    }
    return {
      tone: 'danger',
      title: message ?? fallback,
      detail:
        action === 'save'
          ? 'Nothing was stored, and this test result cannot be saved again. Run the test again ' +
            'from step 3.'
          : 'Nothing about your stored connection was changed.'
    };
  }

  private statusOf(error: unknown): number {
    return (error as { status?: number } | null)?.status ?? 0;
  }

  /** The stable `code` the Nayax connection failures carry, when the body has one. */
  private errorCode(error: unknown): string | null {
    const body = (error as { error?: { code?: string } } | null)?.error;
    return body?.code ?? null;
  }

  /**
   * The caller-safe sentence the API composed. `ProblemDetails` from `DomainExceptionHandler` and
   * from `NayaxConnectionExceptionHandler` carries it as both `message` and `detail`.
   */
  private serverMessage(error: unknown): string | null {
    const body = (error as { error?: unknown } | null)?.error;
    if (typeof body === 'string' && body.trim()) {
      return body;
    }
    const problem = body as { message?: string; detail?: string; title?: string } | null | undefined;
    return problem?.message ?? problem?.detail ?? problem?.title ?? null;
  }
}
