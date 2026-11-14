import { Component, ElementRef, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProviderAvailabilityService } from '../../../provider/provider-availability.service';
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
import { withoutRetiredNayaxConnectionChecks } from '../../../provider/nayax-read-areas';
import { ModalDialogComponent } from '../../shared/modal-dialog/modal-dialog.component';
import {
  NayaxConnectionChecklistComponent,
  nayaxConnectionAreaLabel,
  nayaxConnectionFailureSentence
} from './nayax-connection-checklist.component';
import { NayaxCoreGuideComponent, NayaxCoreGuideSource } from './nayax-core-guide.component';

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
 * The review step's advice for refused areas that asking Nayax for a permission would not fix, one
 * entry per kind of cause, with the areas it covers (issue #681). `Other` is every cause that is not
 * one of the three named: an answer nobody interpreted is not evidence of a missing permission either.
 */
export interface NayaxConnectionRefusalAdvice {
  readonly cause: 'InvalidOrExpiredToken' | 'OperatorIdMismatch' | 'NayaxUnavailable' | 'Other';
  readonly advice: string;
  readonly areas: string[];
}

/** The three steps of the Connect Nayax dialog (issue #1311). */
export type NayaxConnectStep = 1 | 2 | 3;

/**
 * What the token step shows about the last Test press. `invalid-token` and `timeout` are the two
 * outcomes that send an Owner back to an empty token field rather than on to the review: the token
 * itself was refused, or Nayax never answered, so there is nothing to review.
 */
export type NayaxTokenTestState = 'testing' | 'invalid-token' | 'timeout';

/**
 * What the review step says about a completed test, in the order it is decided: an expired draft
 * needs a fresh test whatever it found; a machine list that did not pass cannot be saved; then a
 * save-eligible run is a partial one, an empty-account one or a clean one.
 */
export type NayaxReviewState = 'expired' | 'not-saveable' | 'partial' | 'zero-machines' | 'passed';

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

/** The three steps, in order, for the progress indicator. */
export const NAYAX_CONNECT_STEP_LABELS: readonly string[] = ['Operator ID', 'Lynx token', 'Review & save'];

/**
 * Nayax's official guides, linked beside each step as the place to see its screens (issue #1311).
 * The Operator ID path is the "How to find your Operator ID in Nayax Core" section of the access
 * request guide, and the operator screen showing the field is in the create-operator guide's
 * "Verify creation" section; the token path is the "Access token" section of the Lynx security
 * guide. Their screenshots are not embedded: each shows a demo account's identifiers (see
 * `NayaxCoreGuideComponent`).
 */
export const NAYAX_OPERATOR_ID_GUIDE: NayaxCoreGuideSource = {
  title: 'How to find your Operator ID in Nayax Core',
  url: 'https://devzone.nayax.com/docs/manage-data-operations/lynx-api/request-access#how-to-find-your-operator-id-in-nayax-core'
};
export const NAYAX_OPERATOR_SCREEN_GUIDE: NayaxCoreGuideSource = {
  title: 'Create an operator: verify creation',
  url: 'https://devzone.nayax.com/docs/manage-data-operations/lynx-api/operator/create-operator#verify-creation'
};
export const NAYAX_TOKEN_GUIDE: NayaxCoreGuideSource = {
  title: 'Lynx API security: access token',
  url: 'https://devzone.nayax.com/docs/manage-data-operations/lynx-api/security#access-token'
};

/** Where the Operator ID is, as Nayax's own guide words it. Static markup: control names in bold. */
export const NAYAX_OPERATOR_ID_STEPS: readonly string[] = [
  'Sign in to Nayax Core and go to <strong>Operator</strong> in the left sidebar.',
  'Select your operator from the list and click <strong>Search</strong>.',
  'Your numeric ID is listed under <strong>Company Info</strong> as <strong>Operator Id</strong>.'
];

/** The path to a Lynx token, as Nayax's own guide words it. Static markup: control names in bold. */
export const NAYAX_TOKEN_STEPS: readonly string[] = [
  'Sign in to Nayax Core with your username and password.',
  'Click your username in the top right corner, then choose <strong>Account Settings</strong>.',
  'Open the <strong>Security and Login</strong> tab, next to Account.',
  'Find the <strong>User Tokens</strong> section, below Your Trusted Devices.',
  'If a Lynx token is listed, choose <strong>Show Token</strong> next to it and copy it. If not, ' +
    'choose <strong>Add Token</strong> and select <strong>Lynx Token</strong>.'
];

/**
 * The Owner-only Nayax connection card on Admin → Integrations, and the **Connect Nayax** dialog it
 * launches (issues #538 and #1311), over the endpoints of issues #536, #537 and #1310.
 *
 * **The card stays on the page; setup is a dialog.** The card shows the stored connection, its
 * status and Re-test, and a single launcher: **Connect Nayax** when nothing is stored, **Replace
 * connection** when something is. The dialog has three steps - Operator ID, Lynx token, Review &
 * save - each guide step putting Nayax's own instructions and guide links (**In Nayax Core**) beside
 * the field (**In Restockroo**). Nothing in the dialog touches the stored connection until **Save
 * connection** succeeds.
 *
 * **The token is write-only.** The field on the Lynx token step is bound to no component state at
 * all: it is read from the DOM at the moment Test is pressed, passed once to `POST …/test`, and
 * cleared the moment that call returns, whatever it concluded. Going Back, closing the dialog and
 * destroying the component clear it too, and closing also abandons a test still in flight so its
 * answer can never reach a dialog that was closed. Nothing here writes it to `localStorage`,
 * `sessionStorage`, a cookie, the URL or a component field, and nothing re-sends it - the save
 * carries the server's own opaque draft id, so the credentials stored are exactly the ones the
 * server tested (see docs/architecture.md § Saving a tested connection and re-testing it).
 *
 * **It decides nothing about the connection.** Every check line, cause, machine count and status on
 * screen is the server's own finding. There is no rule here that concludes a token works, no second
 * reading of a Nayax refusal, and no status this component derives: `NotChecked` is rendered as
 * neither a pass nor a failure, and a run whose machine list never passed is not offered for saving
 * because the server refuses it - this only avoids spending a request on a refusal already known.
 * The one thing it drops is a line for a read this application has retired (issue #1310), which only
 * an older stored report can carry and which the server's own answers already ignore.
 *
 * **Owner only, twice over and authoritative in neither place.** The route already requires
 * `Integration.Manage` (`app.routes.ts`), and the endpoints behind every action require it
 * themselves and refuse the request regardless. The capability read here hides a control an Owner's
 * role is the only one that can use; it starts granted so an unanswered read leaves the card
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
  imports: [
    CommonModule,
    FormsModule,
    ModalDialogComponent,
    NayaxConnectionChecklistComponent,
    NayaxCoreGuideComponent
  ],
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
              class="alert mb-4"
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
            <p class="mb-4 text-sm value-muted" data-testid="status-unavailable">
              The current Nayax connection state could not be read. You can still connect Nayax, and
              saving a tested token reports the state the server concluded.
            </p>
          }

          @if (connectionStored && connection) {
            <div data-testid="stored-connection">
              <div class="flex flex-wrap items-center gap-2">
                <h3 class="text-sm font-semibold text-md-gray-800">Stored connection</h3>
                <span [class]="'badge ' + statusBadge(connection.status)" data-testid="stored-status">
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
              <div class="mt-3 flex flex-wrap gap-2">
                <button
                  type="button"
                  class="btn btn-secondary"
                  data-testid="retest"
                  [disabled]="busy || sessionActive"
                  (click)="retest()"
                >{{ retesting ? 'Re-testing…' : 'Re-test this connection' }}</button>
                <button
                  #launcher
                  type="button"
                  class="btn btn-secondary"
                  data-testid="open-connect"
                  [disabled]="busy"
                  (click)="openDialog()"
                >Replace connection</button>
              </div>

              @if (retestResult) {
                <div class="mt-4" data-testid="retest-checklist">
                  <app-nayax-connection-checklist
                    [checks]="retestResult.checks"
                    [machineCount]="retestResult.machineCount"
                  ></app-nayax-connection-checklist>
                </div>
              }
            </div>
          } @else {
            <p class="text-sm value-muted" data-testid="not-connected">
              This business is not connected to Nayax yet. You will need your Nayax operator ID and a
              Lynx token from Nayax Core.
            </p>
            <button
              #launcher
              type="button"
              class="btn btn-primary mt-3"
              data-testid="open-connect"
              [disabled]="busy"
              (click)="openDialog()"
            >Connect Nayax</button>
          }
        }
      </div>
    </section>

    @if (dialogOpen) {
      <app-modal-dialog
        size="wide"
        testId="connect-nayax-dialog"
        [dialogTitle]="replacing ? 'Replace Nayax connection' : 'Connect Nayax'"
        [closeDisabled]="saving"
        (closeRequested)="closeDialog()"
      >
        <ol class="mb-4 flex flex-wrap gap-x-4 gap-y-1 text-sm" aria-label="Progress" data-testid="step-progress">
          @for (label of stepLabels; track label; let index = $index) {
            <li
              class="flex items-center gap-2"
              [class.font-semibold]="step === index + 1"
              [class.text-md-gray-800]="step >= index + 1"
              [class.value-muted]="step < index + 1"
              [attr.aria-current]="step === index + 1 ? 'step' : null"
              [attr.data-testid]="'progress-' + (index + 1)"
            >
              <span
                class="inline-flex h-6 w-6 items-center justify-center rounded-full border text-xs"
                [class.border-md-gray-200]="step < index + 1"
                [class.bg-md-gray-800]="step >= index + 1"
                [class.text-white]="step >= index + 1"
                aria-hidden="true"
              >{{ step > index + 1 || saved ? '✓' : index + 1 }}</span>
              <span>{{ label }}</span>
              @if (step > index + 1 || saved) {
                <span class="sr-only">(done)</span>
              }
            </li>
          }
        </ol>

        @if (dialogNotice) {
          <div
            class="alert mb-4"
            role="alert"
            [class.alert-danger]="dialogNotice.tone === 'danger'"
            [class.alert-warning]="dialogNotice.tone === 'warning'"
            [class.alert-success]="dialogNotice.tone === 'success'"
            data-testid="dialog-notice"
          >
            <p class="alert-title">{{ dialogNotice.title }}</p>
            <p class="mt-1">{{ dialogNotice.detail }}</p>
          </div>
        }

        @if (step === 1) {
          <div class="grid gap-6 lg:grid-cols-12" data-testid="step-1">
            <app-nayax-core-guide
              class="block min-w-0 lg:col-span-7"
              [steps]="operatorIdSteps"
              [sources]="operatorSources"
            ></app-nayax-core-guide>
            <section class="min-w-0 lg:col-span-5" aria-labelledby="nayax-step-1-heading">
              <h3 id="nayax-step-1-heading" class="text-sm font-semibold text-md-gray-800">In Restockroo</h3>
              <div class="field mt-2">
                <label class="field-label" for="nayax-operator-id">Operator ID</label>
                <input
                  id="nayax-operator-id"
                  type="text"
                  inputmode="numeric"
                  autocomplete="off"
                  data-dialog-autofocus
                  [disabled]="busy"
                  [(ngModel)]="operatorId"
                  (keydown.enter)="next()"
                />
                <p class="field-hint">
                  The numeric Operator Id from Nayax Core. It is not a secret, and it is stored with the
                  connection.
                </p>
              </div>
            </section>
          </div>
        }

        @if (step === 2) {
          <div class="grid gap-6 lg:grid-cols-12" data-testid="step-2">
            <app-nayax-core-guide
              class="block min-w-0 lg:col-span-7"
              [steps]="tokenSteps"
              [sources]="tokenSources"
              data-testid="token-instructions"
            ></app-nayax-core-guide>
            <section class="min-w-0 lg:col-span-5" aria-labelledby="nayax-step-2-heading">
              <h3 id="nayax-step-2-heading" class="text-sm font-semibold text-md-gray-800">In Restockroo</h3>
              <div class="field mt-2">
                <label class="field-label" for="nayax-token">Lynx token</label>
                <input
                  #tokenField
                  id="nayax-token"
                  type="password"
                  autocomplete="off"
                  spellcheck="false"
                  data-dialog-autofocus
                  [disabled]="busy"
                  (keydown.enter)="runTest()"
                />
                <p class="field-hint">
                  The token is a secret. It is sent once to test it, cleared from this page as soon as
                  the test comes back, and never stored in this browser. Testing changes no Nayax data
                  and leaves any stored connection untouched.
                </p>
              </div>
              <p class="mt-2 text-xs value-muted">Testing for operator ID {{ operatorId.trim() }}.</p>

              <div class="mt-3" aria-live="polite" data-testid="token-test-state">
                @switch (testState) {
                  @case ('testing') {
                    <p class="flex items-center gap-2 text-sm text-md-gray-800" data-testid="state-testing">
                      <span aria-hidden="true">⏳</span>
                      Testing the connection with Nayax…
                    </p>
                  }
                  @case ('invalid-token') {
                    <div class="alert alert-danger" data-testid="state-invalid-token">
                      <p class="alert-title"><span aria-hidden="true">✗ </span>Nayax refused this token.</p>
                      <p class="mt-1">
                        It is invalid or has expired. In Nayax Core, copy the Lynx token again with Show
                        Token, or add a new one, then paste it above and test again.
                      </p>
                    </div>
                  }
                  @case ('timeout') {
                    <div class="alert alert-warning" data-testid="state-timeout">
                      <p class="alert-title"><span aria-hidden="true">⚠ </span>Nayax did not answer in time.</p>
                      <p class="mt-1">
                        Nothing was learned about this token, and nothing was saved. Paste the token
                        again and test in a few minutes.
                      </p>
                    </div>
                  }
                }
              </div>
            </section>
          </div>
        }

        @if (step === 3) {
          <div data-testid="step-3">
            @if (saved) {
              <div class="alert alert-success" role="status" data-testid="saved">
                <p class="alert-title"><span aria-hidden="true">✓ </span>Nayax connected</p>
                <p class="mt-1">
                  This business now calls Nayax with the token you tested. Its status is on the
                  Integrations page, where you can re-test it at any time.
                </p>
              </div>
            } @else if (result) {
              @switch (reviewState) {
                @case ('expired') {
                  <div class="alert alert-danger" data-testid="review-expired">
                    <p class="alert-title"><span aria-hidden="true">⏱ </span>This test result has expired.</p>
                    <p class="mt-1">
                      A tested token is kept for fifteen minutes only. Go back, paste the token again and
                      test it before saving.
                    </p>
                  </div>
                }
                @case ('not-saveable') {
                  <div class="alert alert-danger" data-testid="machine-list-failed">
                    <p class="alert-title"><span aria-hidden="true">✗ </span>This token cannot be saved.</p>
                    <p class="mt-1">
                      It could not read this business's machine list from Nayax. The checklist says why;
                      fix that, then test again.
                    </p>
                  </div>
                }
                @case ('partial') {
                  <div class="alert alert-warning" data-testid="review-partial">
                    <p class="alert-title"><span aria-hidden="true">⚠ </span>Connection test passed, with some permissions refused.</p>
                    <p class="mt-1">
                      You can save now. The connection is then recorded as having permissions pending,
                      and the features that need the refused areas keep saying so until Nayax grants them
                      and a re-test confirms it.
                    </p>
                  </div>
                }
                @case ('zero-machines') {
                  <div class="alert alert-success" data-testid="review-zero-machines">
                    <p class="alert-title"><span aria-hidden="true">✓ </span>Connection test passed. No machines found yet.</p>
                    <p class="mt-1">
                      You can save now, and re-test once your machines are set up in Nayax so the
                      machine checks can run.
                    </p>
                  </div>
                }
                @case ('passed') {
                  <div class="alert alert-success" data-testid="review-passed">
                    <p class="alert-title"><span aria-hidden="true">✓ </span>Connection test passed.</p>
                    <p class="mt-1">Nayax shows as connected once you save this connection.</p>
                  </div>
                }
              }

              <h3 class="mt-4 mb-2 text-sm font-semibold text-md-gray-800">What this token can do</h3>
              <app-nayax-connection-checklist
                [checks]="result.checks"
                [machineCount]="result.machineCount"
              ></app-nayax-connection-checklist>

              @for (advice of refusalAdvice(); track advice.cause) {
                <div class="alert alert-warning mt-3" [attr.data-testid]="'refusal-advice-' + advice.cause">
                  <p class="alert-title">{{ advice.advice }}</p>
                  <ul class="mt-1 ml-5 list-disc text-sm">
                    @for (area of advice.areas; track area) {
                      <li>{{ area }}</li>
                    }
                  </ul>
                </div>
              }

              @if (permissionRefusals().length > 0) {
                <div class="mt-4 rounded-lg border border-md-gray-200 p-4" data-testid="permission-request">
                  <h3 class="text-sm font-semibold text-md-gray-800">Ask Nayax for the refused permissions</h3>
                  <p class="mt-1 text-sm value-muted">
                    Nayax grants a token's permissions. Copy this request into a support ticket; it names
                    the operator ID you tested and only the areas refused for lack of a permission.
                  </p>
                  <label class="field-label mt-3" for="nayax-support-request">Request to send Nayax</label>
                  <textarea
                    #supportRequestField
                    id="nayax-support-request"
                    class="w-full rounded-lg border border-md-gray-200 p-3 font-mono text-xs"
                    rows="10"
                    readonly
                    data-testid="support-request"
                    [value]="supportRequest()"
                  ></textarea>
                  <button
                    type="button"
                    class="btn btn-secondary mt-2"
                    data-testid="copy-support-request"
                    (click)="copySupportRequest()"
                  >Copy this request</button>
                  <p class="mt-3 text-sm text-md-gray-800">
                    Raise it in the Nayax support portal:
                    <a
                      class="btn-link break-all"
                      data-testid="support-portal-link"
                      target="_blank"
                      rel="noopener noreferrer"
                      [href]="supportPortalUrl"
                    >{{ supportPortalUrl }}</a>
                  </p>
                  <p class="mt-1 text-sm value-muted">
                    If the portal gets no answer, the integration mailbox is the secondary contact:
                    <a class="btn-link break-all" data-testid="support-email-link" [href]="'mailto:' + supportEmail">
                      {{ supportEmail }}
                    </a>
                  </p>
                </div>
              }
            } @else {
              <p class="text-sm value-muted" data-testid="no-result">
                There is no completed test to save. Go back and test the token.
              </p>
            }
          </div>
        }

        <div dialog-actions class="flex w-full flex-wrap items-center justify-between gap-2">
          <div>
            @if (step > 1 && !saved) {
              <button
                type="button"
                class="btn btn-secondary"
                data-testid="back"
                [disabled]="busy"
                (click)="back()"
              >Back</button>
            }
          </div>
          <div class="flex flex-wrap items-center gap-2">
            @if (step === 1) {
              <button type="button" class="btn btn-primary" data-testid="next" [disabled]="busy" (click)="next()">
                Next
              </button>
            }
            @if (step === 2) {
              <span class="hidden text-xs value-muted sm:inline">Nothing is saved yet</span>
              <button
                type="button"
                class="btn btn-primary"
                data-testid="run-test"
                [disabled]="busy || sessionActive"
                (click)="runTest()"
              >{{ testing ? 'Testing…' : 'Test connection' }}</button>
            }
            @if (step === 3 && !saved) {
              @if (reviewState === 'expired') {
                <button type="button" class="btn btn-primary" data-testid="test-again" [disabled]="busy" (click)="back()">
                  Test again
                </button>
              } @else if (result) {
                <button
                  type="button"
                  class="btn btn-primary"
                  data-testid="save-connection"
                  [disabled]="!canSave"
                  (click)="saveConnection()"
                >{{ saving ? 'Saving…' : 'Save connection' }}</button>
              }
            }
            @if (saved) {
              <button type="button" class="btn btn-primary" data-testid="done" (click)="closeDialog()">Done</button>
            }
          </div>
        </div>
      </app-modal-dialog>
    }
  `
})
export class NayaxConnectionWizardComponent implements OnInit, OnDestroy {
  readonly stepLabels = NAYAX_CONNECT_STEP_LABELS;
  readonly supportPortalUrl = NAYAX_SUPPORT_PORTAL_URL;
  readonly supportEmail = NAYAX_SUPPORT_EMAIL;
  readonly operatorIdSteps = NAYAX_OPERATOR_ID_STEPS;
  readonly operatorSources = [NAYAX_OPERATOR_ID_GUIDE, NAYAX_OPERATOR_SCREEN_GUIDE];
  readonly tokenSteps = NAYAX_TOKEN_STEPS;
  readonly tokenSources = [NAYAX_TOKEN_GUIDE];

  /** Whether the Connect Nayax dialog is on screen. */
  dialogOpen = false;

  /** Whether the dialog was opened to replace a stored connection, which only changes its title. */
  replacing = false;

  /** Which dialog step is on screen. */
  step: NayaxConnectStep = 1;

  /** The operator id. Not a secret, and the only credential field this component holds. */
  operatorId = '';

  /**
   * Whether the signed-in member's role may manage this integration. It starts `true` so an
   * unanswered capability read leaves the card exactly as it was before roles were rendered; the
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

  /** True when the stored state could not be read; connecting stays possible regardless. */
  statusUnavailable = false;

  /** The completed test awaiting a save. It carries the draft id and the check lines, never a token. */
  result: NayaxConnectionTestResult | null = null;

  /** What the token step shows about the last Test press, if anything. */
  testState: NayaxTokenTestState | null = null;

  /**
   * The trimmed operator id {@link result} was tested with (issue #681). The support request names
   * this one, not whatever the Operator ID field holds now: the draft a save promotes carries the
   * tested id, so a request pairing an edited id with another id's refusals would ask Nayax about
   * the wrong operator.
   */
  private testedOperatorId = '';

  /** The last re-test of the stored credentials. */
  retestResult: NayaxConnectionRetestResult | null = null;

  testing = false;
  saving = false;
  retesting = false;

  /** True once a draft has been promoted into the connection from the open dialog. */
  saved = false;

  /** The outcome of a re-test or a status change, shown on the card. */
  notice: NayaxConnectionNotice | null = null;

  /** The outcome of a test or save that is not one of the token step's own states, shown in the dialog. */
  dialogNotice: NayaxConnectionNotice | null = null;

  /**
   * The token field. It is read at the moment Test is pressed and cleared when that call returns:
   * there is deliberately no `ngModel`, signal or field holding the token between those two points.
   */
  @ViewChild('tokenField') private readonly tokenField?: ElementRef<HTMLInputElement>;

  /** Connect Nayax or Replace connection, whichever the card shows now. */
  @ViewChild('launcher') private readonly launcher?: ElementRef<HTMLButtonElement>;

  @ViewChild('supportRequestField')
  private readonly supportRequestField?: ElementRef<HTMLTextAreaElement>;

  private statusSubscription: Subscription | null = null;
  private testSubscription: Subscription | null = null;
  private saveSubscription: Subscription | null = null;
  private retestSubscription: Subscription | null = null;
  private readonly manageAccess: Subscription;
  private readonly sessionActiveSubscription: Subscription;

  /**
   * Bumped by every Test press, every Back, and every close, so a test answer that arrives after
   * any of them is recognised as stale and dropped: it can neither reopen the dialog nor fill in a
   * step the Owner has already left.
   */
  private testSequence = 0;

  /** Re-renders the review when the draft expires, so Save is withdrawn on time and not on the next click. */
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  /** Set when {@link expiryTimer} fires: the current draft has lapsed, whatever the clock says now. */
  private draftLapsed = false;

  constructor(
    private readonly connections: NayaxConnectionService,
    private readonly provider: ProviderAvailabilityService,
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
    this.clearExpiryTimer();
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

  /**
   * Whether any line of the completed test failed. It agrees with the server's own `nothingFailed`
   * flag for every report this build's server produces; it is computed from the lines so that a
   * retired read's refusal - which an older stored report can still carry, and which the server's
   * flag already ignores - never puts the screen into "some areas were refused" (issue #1310).
   */
  get anyFailed(): boolean {
    return this.failedChecks().length > 0;
  }

  /**
   * Whether the tested token read this business's machine list. That is the check the credentials
   * are judged by since issue #1310, and the server refuses to save a token that failed it.
   */
  get machineListPassed(): boolean {
    return this.result?.checks.some((line) => line.check === 'Machines' && line.status === 'Passed') ?? false;
  }

  /** Whether the server's short-lived draft has already stopped existing. */
  get draftExpired(): boolean {
    return this.result !== null && (this.draftLapsed || Date.parse(this.result.expiresAtUtc) <= Date.now());
  }

  /** What the review step says about {@link result}; see {@link NayaxReviewState} for the order. */
  get reviewState(): NayaxReviewState | null {
    if (this.result === null) {
      return null;
    }
    if (this.draftExpired) {
      return 'expired';
    }
    if (!this.machineListPassed) {
      return 'not-saveable';
    }
    if (this.anyFailed) {
      return 'partial';
    }
    return this.result.machineCount === 0 ? 'zero-machines' : 'passed';
  }

  get canSave(): boolean {
    return (
      this.canManage &&
      !this.sessionActive &&
      !this.busy &&
      this.result !== null &&
      this.machineListPassed &&
      !this.draftExpired
    );
  }

  statusLabel(status: NayaxConnectionStatusName): string {
    return STATUS_PRESENTATION[status]?.label ?? status;
  }

  statusBadge(status: NayaxConnectionStatusName): string {
    return STATUS_PRESENTATION[status]?.badge ?? 'badge-neutral';
  }

  /** Opens the dialog on its first step with nothing carried over from an earlier attempt. */
  openDialog(): void {
    if (!this.canManage || this.busy) {
      return;
    }
    this.resetAttempt();
    this.operatorId = '';
    this.saved = false;
    this.replacing = this.connectionStored;
    this.notice = null;
    this.step = 1;
    this.dialogOpen = true;
  }

  /**
   * Closes the dialog from Escape, the backdrop, Close or Done. It is refused only while a save is in
   * flight, whose answer decides what is stored. Anything else is abandoned: the token field is
   * cleared, a test in flight is unsubscribed and its sequence retired, and the unsaved result is
   * dropped, so nothing of this attempt survives to be shown, saved or reopened.
   */
  closeDialog(): void {
    if (this.saving) {
      return;
    }
    this.resetAttempt();
    this.dialogOpen = false;
    // The dialog gives focus back to the control that opened it. A save swaps Connect Nayax for
    // Replace connection, so that control may be gone: focus then goes to the one now on the card.
    setTimeout(() => {
      const active = document.activeElement;
      if (active === null || active === document.body) {
        this.launcher?.nativeElement.focus();
      }
    });
  }

  next(): void {
    if (this.step !== 1) {
      return;
    }
    if (!this.operatorId.trim()) {
      this.dialogNotice = {
        tone: 'danger',
        title: 'Enter the operator ID from Nayax Core first.',
        detail: 'Nothing was sent to Nayax. The steps beside the field show where to find it.'
      };
      return;
    }
    this.dialogNotice = null;
    this.step = 2;
  }

  /**
   * One step back. Leaving the token step clears the field; leaving the review drops the result,
   * because the test is of a token that is no longer anywhere on this page and a new one is only
   * reached by testing again.
   */
  back(): void {
    if (this.busy) {
      return;
    }
    if (this.step === 3) {
      this.result = null;
      this.clearExpiryTimer();
      this.testState = null;
      this.dialogNotice = null;
      this.step = 2;
      return;
    }
    if (this.step === 2) {
      this.discardToken();
      this.testSequence++;
      this.testState = null;
      this.dialogNotice = null;
      this.step = 1;
    }
  }

  /**
   * Runs the server's checks with the operator id entered and the token in the field.
   *
   * The token is read here, passed once, and cleared the moment the call returns - on a success, a
   * refusal and a transport failure alike. A token Nayax refused, or a test Nayax never answered,
   * keeps the Owner on this step with the field empty to paste again; every other completed run goes
   * on to the review.
   */
  runTest(): void {
    if (!this.canManage || this.sessionActive || this.busy || this.step !== 2) {
      return;
    }

    const operatorId = this.operatorId.trim();
    if (!operatorId) {
      this.dialogNotice = {
        tone: 'danger',
        title: 'Enter the operator ID from Nayax Core first.',
        detail: 'Nothing was sent to Nayax. Go back and enter the operator ID.'
      };
      return;
    }

    const token = this.tokenField?.nativeElement.value.trim() ?? '';
    if (!token) {
      this.dialogNotice = {
        tone: 'danger',
        title: 'Paste the Lynx token before testing.',
        detail: 'Nothing was sent to Nayax. The steps beside the field show where the token is in Nayax Core.'
      };
      return;
    }

    this.testSubscription?.unsubscribe();
    const sequence = ++this.testSequence;
    this.testing = true;
    this.testState = 'testing';
    this.dialogNotice = null;
    this.result = null;
    this.retestResult = null;

    this.testSubscription = this.connections.test(operatorId, token).subscribe({
      next: (result) => {
        this.discardToken();
        if (sequence !== this.testSequence) return;
        this.testing = false;
        this.testedOperatorId = operatorId;

        const machineList = result.checks.find((line) => line.check === 'Machines');
        if (machineList?.status !== 'Passed' && machineList?.failure === 'InvalidOrExpiredToken') {
          this.testState = 'invalid-token';
          this.focusTokenField();
          return;
        }
        if (machineList?.status !== 'Passed' && machineList?.failure === 'NayaxUnavailable') {
          this.testState = 'timeout';
          this.focusTokenField();
          return;
        }

        this.testState = null;
        this.result = result;
        this.scheduleExpiry(result);
        this.step = 3;
      },
      error: (error: unknown) => {
        this.discardToken();
        if (sequence !== this.testSequence) return;
        this.testing = false;
        const status = this.statusOf(error);
        if (status === 408 || status === 504) {
          this.testState = 'timeout';
        } else {
          this.testState = null;
          this.dialogNotice = this.describeFailure(error, 'The connection test could not be completed.', 'test');
        }
        this.focusTokenField();
      }
    });
  }

  /** Promotes the completed test into this business's connection, by draft id alone. */
  saveConnection(): void {
    const result = this.result;
    if (!this.canManage || this.sessionActive || this.busy || !result) {
      return;
    }
    if (!this.machineListPassed) {
      this.dialogNotice = {
        tone: 'danger',
        title: 'The tested token could not read this business\'s machine list, so it was not saved.',
        detail:
          'A token that has never been shown to read this business\'s machines must not replace ' +
          'what it is using. Fix what the checklist reported, then test again.'
      };
      return;
    }
    if (this.draftExpired) {
      // The review says so on its own; nothing is sent for a draft the server no longer holds.
      return;
    }

    this.saveSubscription?.unsubscribe();
    this.saving = true;
    this.dialogNotice = null;

    this.saveSubscription = this.connections.save(result.draftId).subscribe({
      next: (connection) => {
        this.saving = false;
        // The stored pair is exactly the pair this test ran against, so its check lines are
        // evidence about the connection that now exists (issue #728).
        this.acceptConnection(connection, result.checks);
        this.saved = true;
        // The draft is consumed: withdrawing it is what makes a second save impossible here as
        // well as refused by the server.
        this.result = null;
        this.clearExpiryTimer();
        this.retestResult = null;
        this.toast.success('Nayax connected.');
      },
      error: (error: unknown) => {
        this.saving = false;
        // A draft is single-use, so this one can never be saved again whatever refused it. A fresh
        // test is the only safe next step, and the stored state is re-read rather than assumed.
        this.result = null;
        this.clearExpiryTimer();
        this.step = 2;
        this.dialogNotice = this.describeFailure(error, 'The connection could not be saved.', 'save');
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
        this.acceptConnection(result.connection, result.checks);
        if (!result.statusUpdated && this.machineListRefused(result)) {
          this.notice = {
            tone: 'warning',
            title: 'Nayax refused this token access to the machine list.',
            detail:
              'Nayax answered, but this token does not have permission to read the machine list ' +
              '(GET machines). The stored connection and its last-tested time are unchanged. Ask ' +
              'Nayax for read access to the machine list for this operator, then re-test.'
          };
          return;
        }
        if (!result.statusUpdated) {
          this.notice = {
            tone: 'warning',
            title: 'No usable answer came back from Nayax, so nothing was concluded.',
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
   * The request to send Nayax: the operator id that was tested and every area refused for lack of a
   * permission, with the cause the server reported for each. It names no token, and it is built from
   * the one area/cause vocabulary the checklist uses, so the screen and the ticket cannot say
   * different things. Areas refused for any other cause are {@link refusalAdvice}'s, because no
   * permission Nayax could grant would fix them (issue #681).
   */
  supportRequest(): string {
    const operatorId = this.testedOperatorId || '(operator id not entered)';
    const refused = this.permissionRefusals().map(
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
      'areas were refused for lack of a permission:',
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

  /** The refused lines a permission from Nayax would fix: the only ones the request names. */
  permissionRefusals(): NayaxConnectionCheckLine[] {
    return this.failedChecks().filter((line) => line.failure === 'PermissionNotGranted');
  }

  /**
   * What to do about every other refused line, grouped by cause in the order an Owner should act:
   * a dead token makes every other answer moot, a wrong operator id comes next, and an outage only
   * needs a retry. The causes are the server's; this only chooses the next step each one asks for.
   */
  refusalAdvice(): NayaxConnectionRefusalAdvice[] {
    const areasFor = (cause: NayaxConnectionRefusalAdvice['cause']): string[] =>
      this.failedChecks()
        .filter((line) => line.failure !== 'PermissionNotGranted' && this.adviceCause(line) === cause)
        .map((line) => nayaxConnectionAreaLabel(line.check));

    const operatorId = this.testedOperatorId || '(operator id not entered)';
    const advice: NayaxConnectionRefusalAdvice[] = [
      {
        cause: 'InvalidOrExpiredToken',
        advice:
          'Nayax says this token is invalid or has expired, so asking for permissions will not help. ' +
          'Copy or add a Lynx token in Nayax Core (the Lynx token step) and test it.',
        areas: areasFor('InvalidOrExpiredToken')
      },
      {
        cause: 'OperatorIdMismatch',
        advice:
          `Nayax says operator ID ${operatorId} is not one this token covers. Check the operator ID ` +
          'in Nayax Core (the Operator ID step) and test again.',
        areas: areasFor('OperatorIdMismatch')
      },
      {
        cause: 'NayaxUnavailable',
        advice:
          'Nayax could not be reached for these areas, so nothing was learned about them. Try the ' +
          'test again in a few minutes.',
        areas: areasFor('NayaxUnavailable')
      },
      {
        cause: 'Other',
        advice:
          'Nayax answered these areas in a way this application does not recognise, so it is not ' +
          'known whether a permission is missing. Try the test again before asking Nayax about them.',
        areas: areasFor('Other')
      }
    ];
    return advice.filter((entry) => entry.areas.length > 0);
  }

  /**
   * The lines that were refused. A `NotChecked` line is not one: nothing was asked of it. Neither is
   * a retired read's line (issue #1310) - the refusal is of a permission this application no longer
   * needs, so it earns no advice, no support request and no failure summary.
   */
  private failedChecks(): NayaxConnectionCheckLine[] {
    return withoutRetiredNayaxConnectionChecks(this.result?.checks ?? []).filter(
      (line) => line.status !== 'Passed' && line.status !== 'NotChecked'
    );
  }

  /** Which advice a refused, non-permission line falls under; an unknown or missing cause is `Other`. */
  private adviceCause(line: NayaxConnectionCheckLine): NayaxConnectionRefusalAdvice['cause'] {
    switch (line.failure) {
      case 'InvalidOrExpiredToken':
      case 'OperatorIdMismatch':
      case 'NayaxUnavailable':
        return line.failure;
      default:
        return 'Other';
    }
  }

  /**
   * Whether a re-test's machine list was refused for lack of a permission. The server leaves the
   * stored status and last-tested time untouched for that answer just as it does when Nayax never
   * answered (issue #1310), but Nayax did answer, so the two need different guidance.
   */
  private machineListRefused(result: NayaxConnectionRetestResult): boolean {
    return result.checks.some(
      (line) => line.check === 'Machines' && line.status !== 'Passed' && line.failure === 'PermissionNotGranted'
    );
  }

  /** Forgets everything of the current attempt: the token, a test in flight, its result and notices. */
  private resetAttempt(): void {
    this.discardToken();
    this.testSubscription?.unsubscribe();
    this.testSubscription = null;
    this.testSequence++;
    this.testing = false;
    this.testState = null;
    this.result = null;
    this.testedOperatorId = '';
    this.dialogNotice = null;
    this.clearExpiryTimer();
  }

  private scheduleExpiry(result: NayaxConnectionTestResult): void {
    this.clearExpiryTimer();
    const remaining = Date.parse(result.expiresAtUtc) - Date.now();
    if (Number.isFinite(remaining) && remaining > 0) {
      this.expiryTimer = setTimeout(() => {
        this.expiryTimer = null;
        this.draftLapsed = true;
      }, remaining + 50);
    }
  }

  private clearExpiryTimer(): void {
    this.draftLapsed = false;
    if (this.expiryTimer !== null) {
      clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
  }

  private loadStatus(): void {
    this.statusSubscription?.unsubscribe();
    this.statusSubscription = this.connections.status().subscribe({
      next: (connection) => this.acceptConnection(connection),
      error: () => (this.statusUnavailable = true)
    });
  }

  /**
   * Records a connection state the server just reported - a fresh status read, a completed save, a
   * re-test - on this screen and on the application-wide signal every other screen shares (issue
   * #728). This is the one place the connection changes, so it is the one place that signal needs
   * refreshing: without it a banner, a machine filter or an expense category read earlier in the
   * session would keep describing the connection the Owner has just replaced.
   *
   * `checks` is the completed run's own check lines, which a save and a re-test have and a plain
   * status read does not. They are the only per-read evidence this application ever obtains about
   * which Nayax permissions the stored credentials have, so they refresh the shared signal's
   * per-feature answers; without them those answers are cleared rather than carried over.
   */
  private acceptConnection(
    connection: NayaxConnectionState,
    checks?: readonly NayaxConnectionCheckLine[]
  ): void {
    this.connection = connection;
    this.statusUnavailable = false;
    this.provider.accept(connection, checks);
  }

  private discardToken(): void {
    const field = this.tokenField?.nativeElement;
    if (field) {
      field.value = '';
    }
  }

  /** Puts focus back in the (now empty) token field, ready for the token to be pasted again. */
  private focusTokenField(): void {
    setTimeout(() => this.tokenField?.nativeElement.focus());
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
            ? 'Whether the connection was stored is not known from here, so the stored state on the ' +
              'Integrations page was re-read. Check it before testing again.'
            : 'Nothing was concluded about your Nayax credentials. Check the connection and try again.'
      };
    }
    return {
      tone: 'danger',
      title: message ?? fallback,
      detail:
        action === 'save'
          ? 'Nothing was stored, and this test result cannot be saved again. Paste the token and test ' +
            'it again.'
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
