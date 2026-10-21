import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { nayaxConnectionAreaLabel } from '../../../provider/nayax-read-areas';
import { NayaxConnectionCheckFailureName, NayaxConnectionCheckLine } from '../../../services/nayax-connection.service';

/**
 * The area label this checklist renders. The vocabulary itself moved to
 * `provider/nayax-read-areas.ts` when issue #729's locked menu item and "needs a provider" page
 * became its second reader: one refusal must be worded once, wherever it is shown. It is re-exported
 * here because this is the module the wizard and its specs already name.
 */
export { nayaxConnectionAreaLabel };

/**
 * What one failure cause means, in Nayax's own documented vocabulary as
 * `Inventory.Domain.Nayax.NayaxConnectionCheckFailure` publishes it. A `401` is a verdict on the
 * token, a `403` is a verdict on one endpoint's permission, and nothing learned is nothing
 * concluded - so each cause reads as its own sentence and never as a near-enough one.
 */
const FAILURE_SENTENCES: Record<NayaxConnectionCheckFailureName, string> = {
  InvalidOrExpiredToken: 'The token is invalid or has expired.',
  PermissionNotGranted: 'This token does not have permission for this area.',
  OperatorIdMismatch: 'This operator id is not one this token covers.',
  NayaxUnavailable: 'Nayax could not be reached, so nothing was learned about this area.',
  UnexpectedResponse: 'Nayax answered something this application does not recognise.'
};

/**
 * The cause sentence, or a sentence that claims nothing for a cause this bundle does not know.
 * An uninterpreted refusal must never be presented as one that has been interpreted.
 */
export function nayaxConnectionFailureSentence(failure: NayaxConnectionCheckFailureName | null): string {
  if (failure === null) {
    return 'Nayax refused this area and reported no cause.';
  }
  return FAILURE_SENTENCES[failure] ?? `Nayax refused this area: ${failure}.`;
}

/** One rendered line: the server's own status, with the sentence that explains it. */
interface ChecklistRow {
  readonly check: string;
  readonly label: string;
  readonly symbol: string;
  readonly statusLabel: string;
  readonly tone: 'passed' | 'failed' | 'not-checked';
  readonly detail: string | null;
}

/**
 * Step 4 of the Nayax connection wizard (issue #538): the checklist of a completed Test or
 * Re-test run.
 *
 * **It is presentation only.** Every status, cause and machine count it shows is the server's own
 * finding (issues #536 and #537): there is no rule here that decides whether a token works, no
 * reinterpretation of a Nayax answer, and no line this component invents. A `NotChecked` line is
 * rendered as neither a pass nor a failure, because an untested permission must not look like a
 * granted one.
 *
 * **The restock write call is never tested**, and the checklist says so: testing a connection must
 * not change an operator's Nayax data, so `POST machines/{id}/machineProducts` is checked the first
 * time a real refill is pushed.
 */
@Component({
  selector: 'app-nayax-connection-checklist',
  standalone: true,
  imports: [CommonModule],
  template: `
    <ul class="flex flex-col gap-2" data-testid="connection-checklist">
      @for (row of rows(); track row.check) {
        <li
          class="rounded-lg border border-md-gray-200 p-3"
          [attr.data-testid]="'check-' + row.check"
        >
          <div class="flex flex-wrap items-center gap-2">
            <span
              class="badge"
              [class.badge-success]="row.tone === 'passed'"
              [class.badge-danger]="row.tone === 'failed'"
              [class.badge-neutral]="row.tone === 'not-checked'"
            >
              <span aria-hidden="true">{{ row.symbol }}</span>
              <span class="ml-1">{{ row.statusLabel }}</span>
            </span>
            <span class="text-sm font-medium text-md-gray-800">{{ row.label }}</span>
          </div>
          @if (row.detail) {
            <p class="mt-1 text-sm value-muted">{{ row.detail }}</p>
          }
        </li>
      }
    </ul>

    @if (machineCount !== null) {
      <p class="mt-3 text-sm value-muted" data-testid="machine-count">
        Machines in this Nayax account: <strong>{{ machineCount }}</strong>. The machine checks run
        against the first one only.
      </p>
    }

    <p class="mt-3 text-sm value-muted" data-testid="restock-note">
      The restock write call (POST machines/{{ '{' }}id{{ '}' }}/machineProducts) is deliberately not
      tested here: a connection test never changes your Nayax data. It is checked on first real use,
      the first time a refill is pushed to a machine.
    </p>
  `
})
export class NayaxConnectionChecklistComponent {
  /** The completed run's lines, in the server's own fixed order. */
  @Input() checks: NayaxConnectionCheckLine[] = [];

  /**
   * How many machines the machine list returned, or `null` when that check did not succeed. It is
   * what tells "this account has no machines yet" apart from "the machine list could not be read",
   * which need completely different actions from an Owner.
   */
  @Input() machineCount: number | null = null;

  rows(): ChecklistRow[] {
    return this.checks.map((line) => this.toRow(line));
  }

  private toRow(line: NayaxConnectionCheckLine): ChecklistRow {
    const label = nayaxConnectionAreaLabel(line.check);
    switch (line.status) {
      case 'Passed':
        return { check: line.check, label, symbol: '✓', statusLabel: 'Passed', tone: 'passed', detail: null };
      case 'NotChecked':
        return {
          check: line.check,
          label,
          symbol: '—',
          statusLabel: 'Not checked',
          tone: 'not-checked',
          detail: this.notCheckedSentence()
        };
      default:
        // Anything that is not a pass and not an explicit "not checked" is shown as a failure: a
        // status this bundle does not know must never be rendered as a granted permission.
        return {
          check: line.check,
          label,
          symbol: '✗',
          statusLabel: 'Failed',
          tone: 'failed',
          detail: nayaxConnectionFailureSentence(line.failure)
        };
    }
  }

  /**
   * Why a machine-scoped line was never run. With a machine count of zero the account simply has
   * no machine to run it against; with no count at all the machine list itself could not be read,
   * and saying "no machines yet" there would send an Owner to set up machines they already have.
   */
  private notCheckedSentence(): string {
    return this.machineCount === 0
      ? 'Not checked: no machines in this Nayax account yet. Re-test once your machines are set up.'
      : 'Not checked: the machine list could not be read, so there was no machine to check against.';
  }
}
