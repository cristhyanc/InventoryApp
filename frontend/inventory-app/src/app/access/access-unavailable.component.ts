import { AsyncPipe } from '@angular/common';
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, map } from 'rxjs';
import { AccountStateAnswer, AccountStateName, AccountStateService } from './account-state.service';

/** What the page tells a person about their own account state, naming no business (issue #529). */
interface AccessUnavailableView {
  readonly contactMessage: string;
  readonly offersInviteOption: boolean;
  readonly offersNewBusinessOption: boolean;
}

const CONTACT_MESSAGE_BY_STATE: Readonly<Record<AccountStateName, string>> = {
  Member: '',
  NoMembership: '',
  ApplicationPending: '',
  ApplicationRejected: '',
  BusinessDeactivated: 'Contact the business Owner to have access restored.',
  MembershipAmbiguous: 'Contact support so this can be resolved.',
  MembershipRevoked: 'Contact the business Owner if you expect to still have access.'
};

function toView(answer: AccountStateAnswer): AccessUnavailableView | null {
  if (answer.status !== 'known') {
    return null;
  }

  const { state, onboardingEnabled } = answer.accountState;
  return {
    contactMessage: CONTACT_MESSAGE_BY_STATE[state],
    offersInviteOption: state === 'MembershipRevoked',
    offersNewBusinessOption: state === 'MembershipRevoked' && onboardingEnabled
  };
}

/**
 * The page a person whose *membership* is unusable - deactivated, revoked or ambiguous - is routed
 * to by {@link accountStateGuard} (issue #529, agreed under #328). It is deliberately not the
 * `/access-not-allowed` page of issue #528, which is for a member in good standing who asked for a
 * page their role does not include.
 *
 * **It names no business.** The account-state endpoint it reads from names none either
 * (`Inventory.Application.Access.AccountStateResult`), so there is nothing here to leak. Sign-out
 * is not rendered by this page at all: the application shell's header carries the one sign-out
 * control on every route, this one included.
 *
 * **The options shown follow the API eligibility rule**, not a frontend guess: `MembershipRevoked`
 * is the one state the backend's membership-resolution policy treats as eligible to redeem an
 * invite or (when `onboardingEnabled`) apply for a new business, so it is the one state this page
 * offers either option for. `BusinessDeactivated` and `MembershipAmbiguous` offer neither - the
 * business or the ambiguity has to be resolved by a human before either path means anything.
 */
@Component({
  selector: 'app-access-unavailable',
  standalone: true,
  imports: [AsyncPipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Your access is unavailable</h1>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          @if (view$ | async; as view) {
            <p class="text-md-body">{{ view.contactMessage }}</p>

            @if (view.offersInviteOption) {
              <div class="mt-4">
                <h2 class="text-md-subtitle font-medium">I have an invite link</h2>
                <p class="text-md-body">Open the invite link you were sent to join that business.</p>
              </div>
            }

            @if (view.offersNewBusinessOption) {
              <div class="mt-4">
                <h2 class="text-md-subtitle font-medium">Start a new business</h2>
                <a routerLink="/welcome" class="btn btn-primary mt-2 inline-flex">Start a new business</a>
              </div>
            }
          }
        </div>
      </section>
    </div>
  `
})
export class AccessUnavailableComponent {
  readonly view$: Observable<AccessUnavailableView | null>;

  constructor(accountState: AccountStateService) {
    this.view$ = accountState.state$.pipe(map(toView));
  }
}
