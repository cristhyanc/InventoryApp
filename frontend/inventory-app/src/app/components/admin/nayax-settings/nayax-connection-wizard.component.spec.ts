import { TestBed } from '@angular/core/testing';
import { NgModel } from '@angular/forms';
import { By } from '@angular/platform-browser';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import {
  NayaxConnectionRetestResult,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionTestResult
} from '../../../services/nayax-connection.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { NayaxConnectionChecklistComponent } from './nayax-connection-checklist.component';
import { NayaxConnectionWizardComponent } from './nayax-connection-wizard.component';

const TOKEN = 'lynx-token-paste-value';

function passingChecks(): NayaxConnectionTestResult['checks'] {
  return [
    { check: 'SignIn', status: 'Passed', failure: null },
    { check: 'Machines', status: 'Passed', failure: null },
    { check: 'Machine', status: 'Passed', failure: null },
    { check: 'ProductCatalogue', status: 'Passed', failure: null },
    { check: 'ProductGroups', status: 'Passed', failure: null },
    { check: 'MachineProducts', status: 'Passed', failure: null },
    { check: 'MachineLastSales', status: 'Passed', failure: null },
    { check: 'MachineLastAlerts', status: 'Passed', failure: null }
  ];
}

function testResult(overrides: Partial<NayaxConnectionTestResult> = {}): NayaxConnectionTestResult {
  return {
    draftId: 'd1f0c2a4-0000-4000-8000-00000000000a',
    expiresAtUtc: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
    machineCount: 2,
    nothingFailed: true,
    checks: passingChecks(),
    ...overrides
  };
}

function state(overrides: Partial<NayaxConnectionState> = {}): NayaxConnectionState {
  return { status: 'NotConfigured', machineChecksPending: false, ...overrides };
}

function retestResult(overrides: Partial<NayaxConnectionRetestResult> = {}): NayaxConnectionRetestResult {
  return {
    connection: state({ status: 'Ready' }),
    statusUpdated: true,
    machineCount: 2,
    nothingFailed: true,
    checks: passingChecks(),
    ...overrides
  };
}

interface ConnectionStub {
  test: jest.Mock;
  save: jest.Mock;
  retest: jest.Mock;
  status: jest.Mock;
}

interface ToastStub {
  success: jest.Mock;
  error: jest.Mock;
  warning: jest.Mock;
  info: jest.Mock;
}

async function render(
  overrides: Partial<Record<keyof ConnectionStub, () => Observable<unknown>>> = {},
  options: { canManage?: boolean; sessionActive?: Observable<boolean> } = {}
) {
  const connections: ConnectionStub = {
    test: jest.fn(overrides.test ?? (() => of(testResult()))),
    save: jest.fn(overrides.save ?? (() => of(state({ status: 'Ready' })))),
    retest: jest.fn(overrides.retest ?? (() => of(retestResult()))),
    status: jest.fn(overrides.status ?? (() => of(state())))
  };
  const toast: ToastStub = {
    success: jest.fn(),
    error: jest.fn(),
    warning: jest.fn(),
    info: jest.fn()
  };

  await TestBed.configureTestingModule({
    imports: [NayaxConnectionWizardComponent],
    providers: [
      { provide: NayaxConnectionService, useValue: connections },
      { provide: ToastService, useValue: toast },
      { provide: AccessService, useValue: { allows: () => of(options.canManage ?? true) } },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxConnectionWizardComponent);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const host = fixture.nativeElement as HTMLElement;

  /** Types the operator id the way the field's `ngModel` would, then settles the view. */
  const enterOperatorId = (operatorId: string): void => {
    component.operatorId = operatorId;
    fixture.detectChanges();
  };

  /** Pastes a token into the DOM field only - nothing in this component binds it to state. */
  const pasteToken = (token: string): HTMLInputElement => {
    const tokenInput = host.querySelector<HTMLInputElement>('#nayax-token');
    if (tokenInput === null) {
      throw new Error('The token field is not on screen.');
    }
    tokenInput.value = token;
    return tokenInput;
  };

  /** Walks to step 3, pastes the token, and runs the Test call. */
  const runTestWith = (token = TOKEN, operatorId = '7788'): HTMLInputElement => {
    enterOperatorId(operatorId);
    component.goTo(3);
    fixture.detectChanges();
    const field = pasteToken(token);
    component.runTest();
    fixture.detectChanges();
    return field;
  };

  return { fixture, host, component, connections, toast, enterOperatorId, pasteToken, runTestWith };
}

/**
 * The Owner-only Nayax connection wizard (issue #538) over the endpoints of issues #536 and #537.
 * The whole specification of this screen is "show the server's findings and ask for exactly the
 * next step": no check, status, cause or eligibility rule is decided here.
 */
describe('NayaxConnectionWizardComponent (issue #538)', () => {
  describe('Step 1: the operator id', () => {
    it('tells an Owner exactly where the operator id is in Nayax Core', async () => {
      const { host } = await render();

      expect(host.textContent).toContain('Operator');
      expect(host.textContent).toContain('Company Info');
      expect(host.textContent).toContain('Operator Id');
    });

    it('refuses to move on without an operator id, and spends no Nayax test', async () => {
      const { component, connections, fixture, host } = await render();

      component.next();
      fixture.detectChanges();

      expect(component.step).toBe(1);
      expect(connections.test).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Enter the operator id');
    });

    it('moves to the token instructions once an operator id is entered', async () => {
      const { component, enterOperatorId } = await render();

      enterOperatorId(' 7788 ');
      component.next();

      expect(component.step).toBe(2);
    });
  });

  describe('Step 2: the token instructions', () => {
    it('lists the numbered path to a Lynx token in Nayax Core', async () => {
      const { component, fixture, host, enterOperatorId } = await render();

      enterOperatorId('7788');
      component.goTo(2);
      fixture.detectChanges();

      const steps = host.querySelectorAll('[data-testid="token-instructions"] li');
      expect(steps.length).toBeGreaterThanOrEqual(5);
      const instructions = host.querySelector('[data-testid="token-instructions"]')?.textContent ?? '';
      expect(instructions).toContain('Account Settings');
      expect(instructions).toContain('Security and Login');
      expect(instructions).toContain('User Tokens');
      expect(instructions).toContain('Add Token');
      expect(instructions).toContain('Lynx Token');
      expect(instructions).toContain('Show Token');
    });

    it('hot-links no Nayax image, so no screenshot is fetched from their site', async () => {
      const { component, fixture, host, enterOperatorId } = await render();

      enterOperatorId('7788');
      component.goTo(2);
      fixture.detectChanges();

      expect(host.querySelectorAll('img')).toHaveLength(0);
      expect(host.innerHTML).not.toContain('nayax.com/');
    });
  });

  describe('Step 3: the write-only token field', () => {
    it('offers a masked field that no component state is bound to', async () => {
      const { component, fixture, host, enterOperatorId } = await render();

      enterOperatorId('7788');
      // The operator id is a two-way bound value and the token deliberately is not: an `ngModel`
      // on the token field would be a component property holding the secret.
      const operatorField = fixture.debugElement.query(By.css('#nayax-operator-id'));
      expect(operatorField.injector.get(NgModel, null)).not.toBeNull();

      component.goTo(3);
      fixture.detectChanges();

      const field = host.querySelector<HTMLInputElement>('#nayax-token');
      expect(field?.type).toBe('password');
      expect(field?.getAttribute('autocomplete')).toBe('off');
      expect(fixture.debugElement.query(By.css('#nayax-token')).injector.get(NgModel, null)).toBeNull();
    });

    it('sends the pasted token to the test endpoint exactly once', async () => {
      const { connections, runTestWith } = await render();

      runTestWith();

      expect(connections.test).toHaveBeenCalledTimes(1);
      expect(connections.test).toHaveBeenCalledWith('7788', TOKEN);
    });

    it('discards the token from the browser when the test call returns', async () => {
      const { runTestWith } = await render();

      const field = runTestWith();

      expect(field.value).toBe('');
    });

    it('discards the token just the same when the test call fails', async () => {
      const { runTestWith } = await render({
        test: () => throwError(() => ({ status: 500, error: {} }))
      });

      const field = runTestWith();

      expect(field.value).toBe('');
    });

    it('keeps the token in no component state and in no browser storage', async () => {
      const setLocal = jest.spyOn(Storage.prototype, 'setItem');
      const setSession = jest.spyOn(window.sessionStorage, 'setItem');
      const { component, runTestWith } = await render();

      runTestWith();

      expect(JSON.stringify(component)).not.toContain(TOKEN);
      expect(Object.values(component).some((value) => value === TOKEN)).toBe(false);
      expect(setLocal.mock.calls.flat().join(' ')).not.toContain(TOKEN);
      expect(setSession.mock.calls.flat().join(' ')).not.toContain(TOKEN);
      setLocal.mockRestore();
      setSession.mockRestore();
    });

    it('refuses to test an empty token field without calling the API', async () => {
      const { component, connections, fixture, host, enterOperatorId } = await render();

      enterOperatorId('7788');
      component.goTo(3);
      fixture.detectChanges();
      component.runTest();
      fixture.detectChanges();

      expect(connections.test).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Paste the Lynx token');
    });

    it('reports a rate-limited test with the allowance it shares with the re-test', async () => {
      const { host, runTestWith } = await render({
        test: () =>
          throwError(() => ({
            status: 429,
            error: { detail: 'This business has tested its Nayax connection too many times recently.' }
          }))
      });

      runTestWith();

      expect(host.textContent).toContain('too many times recently');
      expect(host.textContent).toContain('Test and re-test share');
    });

    it('reports a refused operator id or token without claiming anything was stored', async () => {
      const { component, host, runTestWith } = await render({
        test: () => throwError(() => ({ status: 400, error: { message: 'Enter the Nayax operator id.' } }))
      });

      runTestWith();

      expect(host.textContent).toContain('Enter the Nayax operator id.');
      expect(component.step).toBe(3);
    });
  });

  describe('Step 4: the checklist', () => {
    it('shows the server checklist of the completed run and nothing of its own', async () => {
      const { component, fixture, runTestWith } = await render();

      runTestWith();

      expect(component.step).toBe(4);
      const checklist = fixture.debugElement.query(By.directive(NayaxConnectionChecklistComponent))
        ?.componentInstance as NayaxConnectionChecklistComponent;
      expect(checklist.checks).toHaveLength(8);
      expect(checklist.machineCount).toBe(2);
    });

    it('passes an unchecked machine run through with its machine count of zero', async () => {
      const { fixture, runTestWith } = await render({
        test: () =>
          of(
            testResult({
              machineCount: 0,
              checks: [
                { check: 'SignIn', status: 'Passed', failure: null },
                { check: 'Machines', status: 'Passed', failure: null },
                { check: 'Machine', status: 'NotChecked', failure: null }
              ]
            })
          )
      });

      runTestWith();

      const checklist = fixture.debugElement.query(By.directive(NayaxConnectionChecklistComponent))
        ?.componentInstance as NayaxConnectionChecklistComponent;
      expect(checklist.machineCount).toBe(0);
      expect(fixture.nativeElement.textContent).toContain('no machines in this Nayax account yet');
    });

    it('skips the support step when nothing failed', async () => {
      const { component, runTestWith } = await render();

      runTestWith();
      component.next();

      expect(component.step).toBe(6);
    });
  });

  describe('Step 5: asking Nayax for the missing permissions', () => {
    const refused = () =>
      of(
        testResult({
          nothingFailed: false,
          machineCount: 1,
          checks: [
            { check: 'SignIn', status: 'Passed', failure: null },
            { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' },
            { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' }
          ]
        })
      );

    it('is reached from the checklist whenever any line failed', async () => {
      const { component, runTestWith } = await render({ test: refused });

      runTestWith();
      component.next();

      expect(component.step).toBe(5);
    });

    it('composes a request naming the operator id and every missing area', async () => {
      const { component, runTestWith } = await render({ test: refused });

      runTestWith();
      const request = component.supportRequest();

      expect(request).toContain('7788');
      expect(request).toContain('Machine list (GET machines)');
      expect(request).toContain('Machine recent sales (GET machines/{id}/lastSales)');
      expect(request).toContain('does not have permission');
      expect(request).not.toContain('Signing in');
      expect(request).not.toContain(TOKEN);
    });

    it('offers the Nayax support portal and the integration mailbox as the second contact', async () => {
      const { component, fixture, host, runTestWith } = await render({ test: refused });

      runTestWith();
      component.next();
      fixture.detectChanges();

      const portal = host.querySelector<HTMLAnchorElement>('[data-testid="support-portal-link"]');
      expect(portal?.getAttribute('href')).toBe('https://nayax.service-now.com/csm');
      expect(portal?.getAttribute('rel')).toContain('noopener');
      expect(host.querySelector('[data-testid="support-email-link"]')?.getAttribute('href')).toBe(
        'mailto:integration-support@nayax.com'
      );
      expect(host.textContent).toContain('integration-support@nayax.com');
    });

    it('shows the request in a field an Owner can copy, and copies it on request', async () => {
      const writeText = jest.fn(() => Promise.resolve());
      Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true });
      const { component, fixture, host, toast, runTestWith } = await render({ test: refused });

      runTestWith();
      component.next();
      fixture.detectChanges();
      const field = host.querySelector<HTMLTextAreaElement>('[data-testid="support-request"]');
      component.copySupportRequest();
      await Promise.resolve();

      expect(field?.readOnly).toBe(true);
      expect(field?.value).toContain('7788');
      expect(writeText).toHaveBeenCalledWith(component.supportRequest());
      expect(toast.success).toHaveBeenCalled();
    });

    it('leaves the request selectable when the browser offers no clipboard', async () => {
      Object.defineProperty(window.navigator, 'clipboard', { value: undefined, configurable: true });
      const { component, fixture, toast, runTestWith } = await render({ test: refused });

      runTestWith();
      component.next();
      fixture.detectChanges();
      component.copySupportRequest();

      expect(toast.info).toHaveBeenCalled();
    });
  });

  describe('Step 6: saving the tested connection', () => {
    it('saves with the draft id alone and reports the status the server concluded', async () => {
      const { component, connections, fixture, host, runTestWith } = await render({
        save: () => of(state({ status: 'Ready' }))
      });

      runTestWith();
      component.goTo(6);
      component.saveConnection();
      fixture.detectChanges();

      expect(connections.save).toHaveBeenCalledWith('d1f0c2a4-0000-4000-8000-00000000000a');
      expect(component.connection).toEqual(state({ status: 'Ready' }));
      expect(host.textContent).toContain('Ready');
    });

    it('records the pending machine checks the save reported and asks for a re-test later', async () => {
      const { component, fixture, host, runTestWith } = await render({
        save: () => of(state({ status: 'Ready', machineChecksPending: true }))
      });

      runTestWith();
      component.goTo(6);
      component.saveConnection();
      fixture.detectChanges();

      expect(host.textContent).toContain('Re-test once your machines are set up');
    });

    it('does not offer to save a run whose sign-in failed, and issues no request', async () => {
      const { component, connections, fixture, host, runTestWith } = await render({
        test: () =>
          of(
            testResult({
              nothingFailed: false,
              machineCount: null,
              checks: [{ check: 'SignIn', status: 'Failed', failure: 'InvalidOrExpiredToken' }]
            })
          )
      });

      runTestWith();
      component.goTo(6);
      fixture.detectChanges();
      component.saveConnection();
      fixture.detectChanges();

      expect(connections.save).not.toHaveBeenCalled();
      expect(host.textContent).toContain('did not sign in to Nayax');
    });

    it('saves a run whose sign-in passed but whose permissions are incomplete', async () => {
      const { component, connections, runTestWith } = await render({
        test: () =>
          of(
            testResult({
              nothingFailed: false,
              machineCount: 1,
              checks: [
                { check: 'SignIn', status: 'Passed', failure: null },
                { check: 'ProductGroups', status: 'Failed', failure: 'PermissionNotGranted' }
              ]
            })
          ),
        save: () => of(state({ status: 'PendingPermissions' }))
      });

      runTestWith();
      component.saveConnection();

      expect(connections.save).toHaveBeenCalled();
      expect(component.connection?.status).toBe('PendingPermissions');
    });

    it('asks for a fresh test instead of saving an expired draft', async () => {
      const { component, connections, fixture, host, runTestWith } = await render({
        test: () => of(testResult({ expiresAtUtc: new Date(Date.now() - 1000).toISOString() }))
      });

      runTestWith();
      component.saveConnection();
      fixture.detectChanges();

      expect(connections.save).not.toHaveBeenCalled();
      expect(host.textContent).toContain('expired');
    });

    it('withdraws a refused draft and shows the server refusal, so it is never saved twice', async () => {
      const { component, fixture, host, runTestWith } = await render({
        save: () =>
          throwError(() => ({
            status: 409,
            error: { detail: 'This test result was already used or has expired. Run the test again.' }
          }))
      });

      runTestWith();
      component.saveConnection();
      fixture.detectChanges();

      expect(host.textContent).toContain('already used or has expired');
      expect(component.result).toBeNull();
    });
  });

  describe('The saved connection and its re-test', () => {
    it('reads the stored connection state on arrival', async () => {
      const { connections, host } = await render({ status: () => of(state({ status: 'Ready' })) });

      expect(connections.status).toHaveBeenCalledTimes(1);
      expect(host.textContent).toContain('Ready');
    });

    it('stays usable when the stored state cannot be read', async () => {
      const { host } = await render({ status: () => throwError(() => ({ status: 500 })) });

      expect(host.textContent).toContain('could not be read');
      expect(host.querySelector('#nayax-operator-id')).toBeTruthy();
    });

    it('prompts for a re-test while the machine checks are pending', async () => {
      const { host } = await render({
        status: () => of(state({ status: 'Ready', machineChecksPending: true }))
      });

      expect(host.textContent).toContain('Re-test once your machines are set up');
      expect(host.querySelector('[data-testid="retest"]')).toBeTruthy();
    });

    it('re-tests the stored credentials and shows the new checklist and status', async () => {
      const { component, connections, fixture, host } = await render({
        status: () => of(state({ status: 'NeedsAttention' })),
        retest: () => of(retestResult())
      });

      component.retest();
      fixture.detectChanges();

      expect(connections.retest).toHaveBeenCalledTimes(1);
      expect(component.connection?.status).toBe('Ready');
      expect(host.querySelector('[data-testid="retest-checklist"]')).toBeTruthy();
    });

    it('says the stored status is untouched when Nayax never answered the re-test', async () => {
      const { component, fixture, host } = await render({
        status: () => of(state({ status: 'Ready' })),
        retest: () =>
          of(
            retestResult({
              connection: state({ status: 'Ready' }),
              statusUpdated: false,
              nothingFailed: false,
              machineCount: null,
              checks: [{ check: 'SignIn', status: 'Failed', failure: 'NayaxUnavailable' }]
            })
          )
      });

      component.retest();
      fixture.detectChanges();

      expect(host.textContent).toContain('left exactly as it was');
      expect(component.connection?.status).toBe('Ready');
    });

    it('reports a connection replaced while the re-test ran', async () => {
      const { component, fixture, host } = await render({
        status: () => of(state({ status: 'Ready' })),
        retest: () =>
          throwError(() => ({
            status: 409,
            error: { detail: 'The connection changed while testing. Run the test again.' }
          }))
      });

      component.retest();
      fixture.detectChanges();

      expect(host.textContent).toContain('The connection changed while testing');
    });

    it('still says the connection changed when the refusal carries no sentence', async () => {
      const { component, fixture, host } = await render({
        status: () => of(state({ status: 'Ready' })),
        retest: () => throwError(() => ({ status: 409, error: null }))
      });

      component.retest();
      fixture.detectChanges();

      expect(host.textContent).toContain('The connection changed while testing');
    });

    it('reports a business with nothing stored to re-test', async () => {
      const { component, fixture, host } = await render({
        status: () => of(state({ status: 'Ready' })),
        retest: () =>
          throwError(() => ({
            status: 409,
            error: { code: 'nayax_not_connected', detail: 'Nayax is not connected for this business.' }
          }))
      });

      component.retest();
      fixture.detectChanges();

      expect(host.textContent).toContain('Nayax is not connected');
      expect(host.textContent).not.toContain('The connection changed while testing');
    });

    it('shares the test allowance with the re-test', async () => {
      const { component, fixture, host } = await render({
        status: () => of(state({ status: 'Ready' })),
        retest: () => throwError(() => ({ status: 429, error: { detail: 'Too many tests.' } }))
      });

      component.retest();
      fixture.detectChanges();

      expect(host.textContent).toContain('Test and re-test share');
    });

    it('offers no re-test while nothing is stored', async () => {
      const { host } = await render({ status: () => of(state({ status: 'NotConfigured' })) });

      expect(host.querySelector('[data-testid="retest"]')).toBeNull();
    });
  });

  describe('Owner only', () => {
    it('renders no wizard and no action for a member without Integration.Manage', async () => {
      const { component, connections, host } = await render({}, { canManage: false });

      expect(host.querySelector('#nayax-operator-id')).toBeNull();
      expect(host.textContent).toContain('Owner');

      component.runTest();
      component.saveConnection();
      component.retest();

      expect(connections.test).not.toHaveBeenCalled();
      expect(connections.save).not.toHaveBeenCalled();
      expect(connections.retest).not.toHaveBeenCalled();
    });
  });

  /**
   * Test, Save connection and Re-test while a support session is active (issue #660). A session
   * grants `IntegrationManage`, so these controls would otherwise still render enabled.
   */
  describe('Support session', () => {
    it('disables Test connection while a session is active, and runTest() writes nothing', async () => {
      const { component, connections, fixture, host, enterOperatorId, pasteToken } = await render(
        {},
        { sessionActive: of(true) }
      );

      enterOperatorId('7788');
      component.goTo(3);
      fixture.detectChanges();
      pasteToken(TOKEN);

      expect((host.querySelector('[data-testid="run-test"]') as HTMLButtonElement).disabled).toBe(true);

      component.runTest();

      expect(connections.test).not.toHaveBeenCalled();
    });

    it('disables Re-test while a session is active, and retest() writes nothing', async () => {
      const { component, connections, host } = await render(
        { status: () => of(state({ status: 'Ready' })) },
        { sessionActive: of(true) }
      );

      expect((host.querySelector('[data-testid="retest"]') as HTMLButtonElement).disabled).toBe(true);

      component.retest();

      expect(connections.retest).not.toHaveBeenCalled();
    });

    it('disables Save connection once a session starts after a passing test, and saveConnection() writes nothing', async () => {
      const sessionActive = new Subject<boolean>();
      sessionActive.next(false);
      const { component, connections, fixture, host, runTestWith } = await render({}, { sessionActive });

      runTestWith();
      component.goTo(6);
      fixture.detectChanges();
      expect(component.canSave).toBe(true);

      sessionActive.next(true);
      fixture.detectChanges();

      expect(component.canSave).toBe(false);
      expect((host.querySelector('[data-testid="save-connection"]') as HTMLButtonElement).disabled).toBe(true);

      component.saveConnection();

      expect(connections.save).not.toHaveBeenCalled();
    });
  });
});
