import { TestBed } from '@angular/core/testing';
import { Route, UrlSegment } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import { Observable, firstValueFrom, isObservable, of, throwError } from 'rxjs';
import { signedOutVisitorGuard } from './signed-out-visitor.guard';

const account = { homeAccountId: 'dana', username: 'dana@example.invalid' } as AccountInfo;

async function decide(
  accounts: AccountInfo[],
  handleRedirectObservable: () => Observable<AuthenticationResult | null> = () => of(null)
): Promise<unknown> {
  TestBed.configureTestingModule({
    providers: [{ provide: MsalService, useValue: { instance: { getAllAccounts: () => accounts }, handleRedirectObservable } }]
  });
  const answer = TestBed.runInInjectionContext(() => signedOutVisitorGuard({} as Route, [] as UrlSegment[]));
  return isObservable(answer) ? firstValueFrom(answer) : answer;
}

/** Which of the two pages `/` shows (issue #954). */
describe('signedOutVisitorGuard', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('matches the landing page for a visitor nobody has signed in as', async () => {
    expect(await decide([])).toBe(true);
  });

  it('does not match it for a signed-in person, so / stays their dashboard', async () => {
    expect(await decide([account])).toBe(false);
  });

  it('decides only after the redirect in progress has been handled', async () => {
    const accounts: AccountInfo[] = [];

    const answer = await decide(accounts, () => {
      accounts.push(account);
      return of({ account } as AuthenticationResult);
    });

    expect(answer).toBe(false);
  });

  it('shows the public page when the redirect failed, since it makes no request', async () => {
    expect(await decide([account], () => throwError(() => new Error('interaction_in_progress')))).toBe(true);
  });
});
