import { Component, EventEmitter, Input, Output } from '@angular/core';
import { PRIVACY_POLICY_ROUTE, TERMS_OF_USE_ROUTE } from '../public/legal/legal-routes';

/**
 * The agreement to the published Terms of Use and Privacy Policy that a person places when they
 * register or join a business (issue #1259).
 *
 * <p>The Terms themselves say "By accepting these terms when registering or joining a business, you
 * agree to them", so this is the control that makes that true: an unticked, required checkbox with
 * both documents linked, shown on the business application page and on the invite acceptance page,
 * with the submit button disabled until it is ticked.</p>
 *
 * **One component for both pages, deliberately.** The wording is a published commitment rather than
 * page copy, and two copies of it would be two things to change when the documents are republished.
 * It owns no state and makes no request: the page it is composed into keeps whether the box is
 * ticked, and sends the version it read from `GET /api/me/account-state` - which this component
 * never sees, because what somebody agreed to is the server's fact rather than this control's.
 *
 * **Both links open in a new tab**, so reading the documents never costs a half-filled form, and
 * both carry `rel="noopener"` as every externally-targeted link here does. They are ordinary
 * `href`s rather than router links because a new tab is a fresh document load either way.
 */
@Component({
  selector: 'app-legal-agreement',
  standalone: true,
  template: `
    <div class="field">
      <label class="flex items-start gap-2 text-md-body" [attr.for]="inputId">
        <input
          type="checkbox"
          class="mt-1"
          [id]="inputId"
          name="acceptedLegalTerms"
          [checked]="accepted"
          (change)="onToggle($event)"
          data-testid="legal-agreement"
          required
        />
        <span>
          I agree to the
          <a [href]="termsOfUseUrl" target="_blank" rel="noopener" data-testid="terms-of-use-link">Terms of Use</a>
          and the
          <a [href]="privacyPolicyUrl" target="_blank" rel="noopener" data-testid="privacy-policy-link"
            >Privacy Policy</a
          >
        </span>
      </label>
    </div>
  `
})
export class LegalAgreementComponent {
  /** Whether the box is ticked. Owned by the page, so a submission and the control cannot disagree. */
  @Input() accepted = false;

  @Output() readonly acceptedChange = new EventEmitter<boolean>();

  /**
   * The checkbox's own id, so two of these on one page - which no page has today, and which is
   * exactly the sort of thing a hard-coded id makes silently wrong - can still be labelled.
   */
  @Input() inputId = 'legal-agreement';

  readonly termsOfUseUrl = `/${TERMS_OF_USE_ROUTE}`;
  readonly privacyPolicyUrl = `/${PRIVACY_POLICY_ROUTE}`;

  onToggle(event: Event): void {
    this.acceptedChange.emit((event.target as HTMLInputElement).checked);
  }
}
