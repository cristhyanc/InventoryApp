import { TestBed } from '@angular/core/testing';
import { NgModel } from '@angular/forms';
import { By } from '@angular/platform-browser';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProviderAvailabilityService } from '../../../provider/provider-availability.service';
import {
  NayaxConnectionRetestResult,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionTestResult
} from '../../../services/nayax-connection.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ModalDialogComponent } from '../../shared/modal-dialog/modal-dialog.component';
import { NayaxConnectionChecklistComponent } from './nayax-connection-checklist.component';
import {
  NAYAX_OPERATOR_ID_GUIDE,
  NAYAX_OPERATOR_SCREENSHOTS,
  NAYAX_OPERATOR_SCREEN_GUIDE,
  NAYAX_TOKEN_GUIDE,
  NAYAX_TOKEN_SCREENSHOTS,
  NayaxConnectionWizardComponent
} from './nayax-connection-wizard.component';
import { NAYAX_CORE_URL } from './nayax-core-guide.component';

const TOKEN = 'lynx-token-paste-value';

function passingChecks(): NayaxConnectionTestResult['checks'] {
  return [
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

  /** Opens the Connect Nayax dialog from the card, the way the launcher's click does. */
  const openDialog = (): void => {
    component.openDialog();
    fixture.detectChanges();
  };

  /** Types the operator id the way the field's `ngModel` would, then settles the view. */
  const enterOperatorId = (operatorId: string): void => {
    component.operatorId = operatorId;
    fixture.detectChanges();
  };

  /** Opens the dialog, enters the operator id and moves on to the Lynx token step. */
  const toTokenStep = (operatorId = '7788'): void => {
    if (!component.dialogOpen) {
      openDialog();
    }
    enterOperatorId(operatorId);
    component.next();
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

  /** Walks to the Lynx token step, pastes the token, and runs the Test call. */
  const runTestWith = (token = TOKEN, operatorId = '7788'): HTMLInputElement => {
    toTokenStep(operatorId);
    const field = pasteToken(token);
    component.runTest();
    fixture.detectChanges();
    return field;
  };

  const testId = (id: string): HTMLElement | null => host.querySelector(`[data-testid="${id}"]`);

  return {
    fixture,
    host,
    component,
    connections,
    toast,
    openDialog,
    enterOperatorId,
    toTokenStep,
    pasteToken,
    runTestWith,
    testId
  };
}

/**
 * The card is the one screen where the connection changes, so it is the one screen that has to
 * refresh the application-wide availability signal (issue #728). Every other screen then reflects
 * the new connection without probing for it, and without a stale answer from earlier in the session.
 */
describe('NayaxConnectionWizardComponent refreshes the shared provider signal (issue #728)', () => {
  it('publishes the state a save established, without reading the status again', async () => {
    const { component, connections, runTestWith } = await render({ status: () => of(state()) });
    const provider = TestBed.inject(ProviderAvailabilityService);
    const before = await firstValueFrom(provider.load());
    expect(before.status).toBe('NotConfigured');
    expect(before.connection).toBe('notConnected');

    runTestWith();
    component.saveConnection();

    expect(connections.save).toHaveBeenCalledTimes(1);
    const after = await firstValueFrom(provider.load());
    expect(after.status).toBe('Ready');
    expect(after.connection).toBe('usable');
    // The stored pair is the pair that was tested, so its check lines are the session's evidence.
    expect(after.features.machineFleet).toBe('available');
    expect(connections.status).toHaveBeenCalledTimes(1);
  });

  it('publishes the state a re-test established', async () => {
    const { component, connections } = await render({ status: () => of(state()) });
    const provider = TestBed.inject(ProviderAvailabilityService);

    component.retest();

    expect(connections.retest).toHaveBeenCalledTimes(1);
    const availability = await firstValueFrom(provider.load());
    expect(availability.status).toBe('Ready');
    expect(availability.connection).toBe('usable');
    expect(connections.status).toHaveBeenCalledTimes(1);
  });

  it('publishes the per-feature permissions a re-test found, granted and refused alike', async () => {
    const { component } = await render({
      status: () => of(state()),
      retest: () =>
        of(
          retestResult({
            nothingFailed: false,
            checks: [
              { check: 'Machines', status: 'Passed', failure: null },
              { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' },
              { check: 'MachineProducts', status: 'NotChecked', failure: null }
            ]
          })
        )
    });
    const provider = TestBed.inject(ProviderAvailabilityService);

    component.retest();

    const availability = await firstValueFrom(provider.load());
    expect(availability.connection).toBe('usable');
    expect(availability.features.machineFleet).toBe('available');
    expect(availability.features.machineSales).toBe('permissionNotGranted');
    expect(availability.features.machineStock).toBe('unknown');
  });

  it('publishes nothing when the status read itself failed, leaving the answer unknown', async () => {
    await render({ status: () => throwError(() => new Error('boom')) });
    const provider = TestBed.inject(ProviderAvailabilityService);

    const availability = await firstValueFrom(provider.load());
    expect(availability.status).toBeNull();
    expect(availability.connection).toBe('unknown');
    expect(availability.features.machineFleet).toBe('unknown');
  });
});

/**
 * The Nayax connection card and its Connect Nayax dialog (issues #538 and #1311) over the endpoints
 * of issues #536, #537 and #1310. The whole specification of this screen is "show the server's
 * findings and ask for exactly the next step": no check, status, cause or eligibility rule is
 * decided here.
 */
describe('NayaxConnectionWizardComponent (issues #538 and #1311)', () => {
  describe('The card and its launcher', () => {
    it('offers Connect Nayax when nothing is stored, and no inline wizard', async () => {
      const { host, testId } = await render();

      expect(testId('open-connect')?.textContent).toContain('Connect Nayax');
      expect(testId('connect-nayax-dialog')).toBeNull();
      expect(host.querySelector('#nayax-operator-id')).toBeNull();
      expect(host.querySelector('#nayax-token')).toBeNull();
    });

    it('offers Replace connection beside Re-test when a connection is stored', async () => {
      const { component, fixture, testId } = await render({ status: () => of(state({ status: 'Ready' })) });

      expect(testId('open-connect')?.textContent).toContain('Replace connection');
      expect(testId('retest')).toBeTruthy();

      (testId('open-connect') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(component.dialogOpen).toBe(true);
      expect(testId('connect-nayax-dialog')?.textContent).toContain('Replace Nayax connection');
    });

    it('opens the dialog on the Operator ID step with an empty field', async () => {
      const { component, fixture, host, testId } = await render();

      (testId('open-connect') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(component.step).toBe(1);
      expect(testId('connect-nayax-dialog')?.textContent).toContain('Connect Nayax');
      expect(host.querySelector<HTMLInputElement>('#nayax-operator-id')?.value).toBe('');
    });

    it('shows three steps with the current one marked', async () => {
      const { openDialog, testId, toTokenStep } = await render();

      openDialog();
      const progress = testId('step-progress');
      expect(progress?.querySelectorAll('li')).toHaveLength(3);
      expect(progress?.textContent).toContain('Operator ID');
      expect(progress?.textContent).toContain('Lynx token');
      expect(progress?.textContent).toContain('Review & save');
      expect(testId('progress-1')?.getAttribute('aria-current')).toBe('step');

      toTokenStep();
      expect(testId('progress-1')?.getAttribute('aria-current')).toBeNull();
      expect(testId('progress-2')?.getAttribute('aria-current')).toBe('step');
    });

    it('closes on the dialog close request, which is what Escape and the Close control send', async () => {
      const { component, fixture, openDialog, testId } = await render();

      openDialog();
      const dialog = fixture.debugElement.query(By.directive(ModalDialogComponent))
        .componentInstance as ModalDialogComponent;
      dialog.requestClose();
      fixture.detectChanges();

      expect(component.dialogOpen).toBe(false);
      expect(testId('connect-nayax-dialog')).toBeNull();
    });
  });

  describe('Step 1: the operator id', () => {
    it('puts Nayax Core instructions beside the field, with Open Nayax Core in a new tab', async () => {
      const { host, openDialog, testId } = await render();

      openDialog();

      const step = testId('step-1');
      expect(step?.textContent).toContain('In Nayax Core');
      expect(step?.textContent).toContain('In Restockroo');
      expect(step?.textContent).toContain('Operator');
      expect(step?.textContent).toContain('Company Info');
      expect(step?.textContent).toContain('Operator Id');
      const open = host.querySelector<HTMLAnchorElement>('[data-testid="step-1"] [data-testid="open-nayax-core"]');
      expect(open?.getAttribute('href')).toBe(NAYAX_CORE_URL);
      expect(open?.getAttribute('target')).toBe('_blank');
      expect(open?.getAttribute('rel')).toContain('noopener');
    });

    it('shows the official operator screen and links the official guides it comes from', async () => {
      const { host, openDialog } = await render();

      openDialog();

      const images = host.querySelectorAll<HTMLImageElement>('[data-testid="step-1"] img');
      expect(images).toHaveLength(NAYAX_OPERATOR_SCREENSHOTS.length);
      expect(images[0].getAttribute('src')).toBe(NAYAX_OPERATOR_SCREENSHOTS[0].src);
      expect(images[0].alt).toContain('Operator Id');
      const source = host.querySelector('[data-testid="step-1"] [data-testid="nayax-guide-source"]');
      const links = Array.from(source?.querySelectorAll('a') ?? []).map((link) => link.getAttribute('href'));
      expect(links).toEqual([NAYAX_OPERATOR_ID_GUIDE.url, NAYAX_OPERATOR_SCREEN_GUIDE.url]);
    });

    it('refuses to move on without an operator id, and spends no Nayax test', async () => {
      const { component, connections, fixture, openDialog, testId } = await render();

      openDialog();
      component.next();
      fixture.detectChanges();

      expect(component.step).toBe(1);
      expect(connections.test).not.toHaveBeenCalled();
      expect(testId('dialog-notice')?.textContent).toContain('Enter the operator ID');
    });

    it('moves to the Lynx token step once an operator id is entered', async () => {
      const { component, toTokenStep } = await render();

      toTokenStep(' 7788 ');

      expect(component.step).toBe(2);
    });
  });

  describe('Step 2: the Lynx token', () => {
    it('lists the numbered path to a Lynx token in Nayax Core beside the field', async () => {
      const { host, toTokenStep, testId } = await render();

      toTokenStep();

      const steps = host.querySelectorAll('[data-testid="nayax-core-steps"] li');
      expect(steps).toHaveLength(5);
      const instructions = testId('token-instructions')?.textContent ?? '';
      expect(instructions).toContain('Account Settings');
      expect(instructions).toContain('Security and Login');
      expect(instructions).toContain('User Tokens');
      expect(instructions).toContain('Add Token');
      expect(instructions).toContain('Lynx Token');
      expect(instructions).toContain('Show Token');
      expect(testId('step-2')?.textContent).toContain('In Restockroo');
      expect(host.querySelector('[data-testid="step-2"] [data-testid="open-nayax-core"]')).toBeTruthy();
    });

    it('shows the official token screenshots, credits the guide and never the token-revealing one', async () => {
      const { host, toTokenStep } = await render();

      toTokenStep();

      const images = Array.from(host.querySelectorAll<HTMLImageElement>('[data-testid="step-2"] img'));
      expect(images.map((image) => image.getAttribute('src'))).toEqual(NAYAX_TOKEN_SCREENSHOTS.map((shot) => shot.src));
      expect(images.map((image) => image.alt)).toEqual([
        expect.stringContaining('Account Settings'),
        expect.stringContaining('Security and Login'),
        expect.stringContaining('User Tokens')
      ]);
      // The guide's fourth screenshot is the Show Token pop-up, which displays a token and its QR code.
      expect(host.innerHTML).not.toContain('security/image4');
      for (const image of images) {
        expect(image.getAttribute('referrerpolicy')).toBe('no-referrer');
      }
      const source = host.querySelector('[data-testid="step-2"] [data-testid="nayax-guide-source"] a');
      expect(source?.getAttribute('href')).toBe(NAYAX_TOKEN_GUIDE.url);
    });

    it('lets each screenshot be opened full size in a new tab', async () => {
      const { host, toTokenStep } = await render();

      toTokenStep();

      const enlarge = Array.from(host.querySelectorAll<HTMLAnchorElement>('[data-testid="step-2"] [data-testid="enlarge-screenshot"]'));
      expect(enlarge.map((link) => link.getAttribute('href'))).toEqual(NAYAX_TOKEN_SCREENSHOTS.map((shot) => shot.src));
      for (const link of enlarge) {
        expect(link.getAttribute('target')).toBe('_blank');
        expect(link.getAttribute('aria-label')).toContain('Enlarge screenshot');
      }
    });

    it('keeps the written steps and says so when a screenshot fails to load', async () => {
      const { fixture, host, toTokenStep } = await render();

      toTokenStep();
      host.querySelector('[data-testid="step-2"] img')?.dispatchEvent(new Event('error'));
      fixture.detectChanges();

      expect(host.querySelectorAll('[data-testid="step-2"] img')).toHaveLength(NAYAX_TOKEN_SCREENSHOTS.length - 1);
      expect(host.querySelector('[data-testid="screenshot-unavailable"]')?.textContent).toContain('steps above are complete');
      expect(host.querySelectorAll('[data-testid="nayax-core-steps"] li')).toHaveLength(5);
    });

    it('offers an empty masked field that no component state is bound to', async () => {
      const { component, fixture, host, openDialog, enterOperatorId } = await render();

      openDialog();
      enterOperatorId('7788');
      // The operator id is a two-way bound value and the token deliberately is not: an `ngModel`
      // on the token field would be a component property holding the secret.
      const operatorField = fixture.debugElement.query(By.css('#nayax-operator-id'));
      expect(operatorField.injector.get(NgModel, null)).not.toBeNull();

      component.next();
      fixture.detectChanges();

      const field = host.querySelector<HTMLInputElement>('#nayax-token');
      expect(field?.type).toBe('password');
      expect(field?.value).toBe('');
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

      expect(JSON.stringify(component, safeJson())).not.toContain(TOKEN);
      expect(Object.values(component).some((value) => value === TOKEN)).toBe(false);
      expect(setLocal.mock.calls.flat().join(' ')).not.toContain(TOKEN);
      expect(setSession.mock.calls.flat().join(' ')).not.toContain(TOKEN);
      setLocal.mockRestore();
      setSession.mockRestore();
    });

    it('refuses to test an empty token field without calling the API', async () => {
      const { component, connections, fixture, toTokenStep, testId } = await render();

      toTokenStep();
      component.runTest();
      fixture.detectChanges();

      expect(connections.test).not.toHaveBeenCalled();
      expect(testId('dialog-notice')?.textContent).toContain('Paste the Lynx token');
    });

    it('shows the testing state while the test runs, and nothing is saved yet', async () => {
      const pending = new Subject<NayaxConnectionTestResult>();
      const { component, runTestWith, testId } = await render({ test: () => pending });

      runTestWith();

      expect(component.testing).toBe(true);
      expect(testId('state-testing')?.textContent).toContain('Testing the connection');
      expect((testId('run-test') as HTMLButtonElement).disabled).toBe(true);
      expect((testId('back') as HTMLButtonElement).disabled).toBe(true);
    });

    it('keeps an Owner on an empty token field when Nayax refused the token', async () => {
      const { component, host, runTestWith, testId } = await render({
        test: () =>
          of(
            testResult({
              nothingFailed: false,
              machineCount: null,
              checks: [
                { check: 'Machines', status: 'Failed', failure: 'InvalidOrExpiredToken' },
                { check: 'Machine', status: 'NotChecked', failure: null }
              ]
            })
          )
      });

      runTestWith();

      expect(component.step).toBe(2);
      expect(component.result).toBeNull();
      expect(testId('state-invalid-token')?.textContent).toContain('invalid or has expired');
      expect(testId('state-timeout')).toBeNull();
      expect(host.querySelector<HTMLInputElement>('#nayax-token')?.value).toBe('');
    });

    it('keeps an Owner on an empty token field, with different guidance, when Nayax did not answer', async () => {
      const { component, host, runTestWith, testId } = await render({
        test: () =>
          of(
            testResult({
              nothingFailed: false,
              machineCount: null,
              checks: [{ check: 'Machines', status: 'Failed', failure: 'NayaxUnavailable' }]
            })
          )
      });

      runTestWith();

      expect(component.step).toBe(2);
      expect(testId('state-timeout')?.textContent).toContain('did not answer in time');
      expect(testId('state-invalid-token')).toBeNull();
      expect(host.querySelector<HTMLInputElement>('#nayax-token')?.value).toBe('');
    });

    it('treats a gateway timeout on the test request as the timeout state', async () => {
      const { component, runTestWith, testId } = await render({
        test: () => throwError(() => ({ status: 504, error: null }))
      });

      runTestWith();

      expect(component.step).toBe(2);
      expect(testId('state-timeout')).toBeTruthy();
    });

    it('reports a rate-limited test with the allowance it shares with the re-test', async () => {
      const { runTestWith, testId } = await render({
        test: () =>
          throwError(() => ({
            status: 429,
            error: { detail: 'This business has tested its Nayax connection too many times recently.' }
          }))
      });

      runTestWith();

      expect(testId('dialog-notice')?.textContent).toContain('too many times recently');
      expect(testId('dialog-notice')?.textContent).toContain('Test and re-test share');
    });

    it('reports a refused operator id or token without claiming anything was stored', async () => {
      const { component, runTestWith, testId } = await render({
        test: () => throwError(() => ({ status: 400, error: { message: 'Enter the Nayax operator id.' } }))
      });

      runTestWith();

      expect(testId('dialog-notice')?.textContent).toContain('Enter the Nayax operator id.');
      expect(component.step).toBe(2);
    });

    it('clears the token field when the Owner goes back to the operator id', async () => {
      const { component, fixture, toTokenStep, pasteToken } = await render();

      toTokenStep();
      const field = pasteToken(TOKEN);
      component.back();
      fixture.detectChanges();

      expect(component.step).toBe(1);
      expect(field.value).toBe('');
    });
  });

  describe('Step 3: review and save', () => {
    const partial = () =>
      of(
        testResult({
          nothingFailed: false,
          machineCount: 1,
          checks: [
            { check: 'Machines', status: 'Passed', failure: null },
            { check: 'ProductGroups', status: 'Failed', failure: 'PermissionNotGranted' },
            { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' }
          ]
        })
      );

    it('shows a clean run as passed, with Save connection and the server checklist', async () => {
      const { component, fixture, runTestWith, testId } = await render();

      runTestWith();

      expect(component.step).toBe(3);
      expect(testId('review-passed')?.textContent).toContain('Connection test passed.');
      expect(testId('review-passed')?.textContent).not.toContain('Nayax connected');
      const checklist = fixture.debugElement.query(By.directive(NayaxConnectionChecklistComponent))
        ?.componentInstance as NayaxConnectionChecklistComponent;
      expect(checklist.checks).toHaveLength(7);
      expect(checklist.machineCount).toBe(2);
      expect((testId('save-connection') as HTMLButtonElement).disabled).toBe(false);
      expect(testId('permission-request')).toBeNull();
    });

    it('shows an empty machine list as passed with no machines, and still offers Save connection', async () => {
      const { component, connections, fixture, runTestWith, testId } = await render({
        test: () =>
          of(
            testResult({
              machineCount: 0,
              checks: [
                { check: 'Machines', status: 'Passed', failure: null },
                { check: 'ProductCatalogue', status: 'Passed', failure: null },
                { check: 'Machine', status: 'NotChecked', failure: null }
              ]
            })
          )
      });

      runTestWith();

      expect(testId('review-zero-machines')?.textContent).toContain('Connection test passed. No machines found yet.');
      expect(fixture.nativeElement.textContent).toContain('no machines in this Nayax account yet');
      const save = testId('save-connection') as HTMLButtonElement;
      expect(save.disabled).toBe(false);

      save.click();

      expect(connections.save).toHaveBeenCalledTimes(1);
      expect(component.saved).toBe(true);
    });

    it('shows the machine list once and no retired sign-in row', async () => {
      const { host, runTestWith } = await render();

      runTestWith();

      expect(host.querySelectorAll('[data-testid="check-Machines"]')).toHaveLength(1);
      expect(host.textContent).not.toContain('Signing in');
      expect(host.textContent).not.toContain('GET devices');
    });

    it('keeps Save connection for a partial run and asks Nayax only for the refused permissions', async () => {
      const { component, runTestWith, testId } = await render({ test: partial });

      runTestWith();

      expect(testId('review-partial')?.textContent).toContain('some permissions refused');
      expect((testId('save-connection') as HTMLButtonElement).disabled).toBe(false);
      const request = component.supportRequest();
      expect(request).toContain('7788');
      expect(request).toContain('Operator product groups');
      expect(request).toContain('Machine recent sales');
      expect(request).not.toContain('Machine list');
      expect(request).not.toContain('Signing in');
      expect(request).not.toContain(TOKEN);
      expect((testId('support-request') as HTMLTextAreaElement).value).toBe(request);
    });

    it('saves a partial run, which the server records as permissions pending', async () => {
      const { component, connections, runTestWith } = await render({
        test: partial,
        save: () => of(state({ status: 'PendingPermissions' }))
      });

      runTestWith();
      component.saveConnection();

      expect(connections.save).toHaveBeenCalled();
      expect(component.connection?.status).toBe('PendingPermissions');
    });

    it('names the operator id that was tested in the request', async () => {
      const { component, runTestWith } = await render({ test: partial });

      runTestWith(TOKEN, ' 7788 ');
      component.operatorId = '9999';

      const request = component.supportRequest();
      expect(request).toContain('Operator ID: 7788');
      expect(request).not.toContain('9999');
    });

    it('asks Nayax only for areas refused for lack of a permission, with its own advice for the rest', async () => {
      const { component, host, runTestWith } = await render({
        test: () =>
          of(
            testResult({
              nothingFailed: false,
              checks: [
                { check: 'Machines', status: 'Passed', failure: null },
                { check: 'ProductGroups', status: 'Failed', failure: 'PermissionNotGranted' },
                { check: 'ProductCatalogue', status: 'Failed', failure: 'OperatorIdMismatch' },
                { check: 'MachineLastSales', status: 'Failed', failure: 'NayaxUnavailable' }
              ]
            })
          )
      });

      runTestWith();

      const request = component.supportRequest();
      expect(request).toContain('Operator product groups');
      expect(request).not.toContain('Operator product catalogue');
      expect(request).not.toContain('Machine recent sales');

      const mismatch = host.querySelector('[data-testid="refusal-advice-OperatorIdMismatch"]');
      expect(mismatch?.textContent).toContain('operator ID 7788');
      expect(mismatch?.textContent).toContain('Check the operator ID');
      expect(mismatch?.textContent).toContain('Operator product catalogue');
      const unavailable = host.querySelector('[data-testid="refusal-advice-NayaxUnavailable"]');
      expect(unavailable?.textContent).toContain('Try the test again');
      expect(unavailable?.textContent).toContain('Machine recent sales');
      expect(host.querySelector('[data-testid="refusal-advice-InvalidOrExpiredToken"]')).toBeNull();
    });

    it('offers the Nayax support portal and the integration mailbox as the second contact', async () => {
      const { host, runTestWith } = await render({ test: partial });

      runTestWith();

      const portal = host.querySelector<HTMLAnchorElement>('[data-testid="support-portal-link"]');
      expect(portal?.getAttribute('href')).toBe('https://nayax.service-now.com/csm');
      expect(portal?.getAttribute('rel')).toContain('noopener');
      expect(host.querySelector('[data-testid="support-email-link"]')?.getAttribute('href')).toBe(
        'mailto:integration-support@nayax.com'
      );
    });

    it('copies the request on request', async () => {
      const writeText = jest.fn(() => Promise.resolve());
      Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true });
      const { component, toast, runTestWith, testId } = await render({ test: partial });

      runTestWith();
      component.copySupportRequest();
      await Promise.resolve();

      expect((testId('support-request') as HTMLTextAreaElement).readOnly).toBe(true);
      expect(writeText).toHaveBeenCalledWith(component.supportRequest());
      expect(toast.success).toHaveBeenCalled();
    });

    it('leaves the request selectable when the browser offers no clipboard', async () => {
      Object.defineProperty(window.navigator, 'clipboard', { value: undefined, configurable: true });
      const { component, toast, runTestWith } = await render({ test: partial });

      runTestWith();
      component.copySupportRequest();

      expect(toast.info).toHaveBeenCalled();
    });

    /**
     * A machine list the token has no permission for, or one refused for its operator id, cannot be
     * saved (issue #1310). The review says which, without spending a request the server would refuse.
     */
    it.each(['PermissionNotGranted', 'OperatorIdMismatch'] as const)(
      'does not offer to save a run whose machine list failed with %s, and issues no request',
      async (failure) => {
        const { component, connections, fixture, runTestWith, testId } = await render({
          test: () =>
            of(
              testResult({
                nothingFailed: false,
                machineCount: null,
                checks: [{ check: 'Machines', status: 'Failed', failure }]
              })
            )
        });

        runTestWith();
        component.saveConnection();
        fixture.detectChanges();

        expect(component.step).toBe(3);
        expect(connections.save).not.toHaveBeenCalled();
        expect(component.canSave).toBe(false);
        expect(testId('machine-list-failed')?.textContent).toContain('could not read this business\'s machine list');
        expect((testId('save-connection') as HTMLButtonElement).disabled).toBe(true);
      }
    );

    /**
     * A draft tested before the machine-list gate shipped, saved after it: the retired sign-in line
     * decides nothing here either. A readable machine list is what offers the save, and the retired
     * line's own refusal is not shown as a failed area (issue #1310).
     */
    it('saves a legacy run whose only refusal was the retired devices probe', async () => {
      const { component, connections, host, runTestWith } = await render({
        test: () =>
          of(
            testResult({
              checks: [
                {
                  check: 'SignIn',
                  status: 'Failed',
                  failure: 'PermissionNotGranted'
                } as unknown as NayaxConnectionTestResult['checks'][number],
                ...passingChecks()
              ]
            })
          )
      });

      runTestWith();

      expect(component.anyFailed).toBe(false);
      expect(component.canSave).toBe(true);
      expect(host.textContent).not.toContain('Signing in');

      component.saveConnection();

      expect(connections.save).toHaveBeenCalled();
    });

    it('requires a fresh test for an expired draft and sends nothing', async () => {
      const { component, connections, fixture, runTestWith, testId } = await render({
        test: () => of(testResult({ expiresAtUtc: new Date(Date.now() - 1000).toISOString() }))
      });

      runTestWith();
      component.saveConnection();
      fixture.detectChanges();

      expect(connections.save).not.toHaveBeenCalled();
      expect(testId('review-expired')?.textContent).toContain('expired');
      expect(testId('save-connection')).toBeNull();

      (testId('test-again') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(component.step).toBe(2);
      expect(component.result).toBeNull();
    });

    it('withdraws Save connection when the draft expires while the review is open', async () => {
      jest.useFakeTimers();
      try {
        const { fixture, runTestWith, testId } = await render({
          test: () => of(testResult({ expiresAtUtc: new Date(Date.now() + 60_000).toISOString() }))
        });

        runTestWith();
        expect(testId('save-connection')).toBeTruthy();

        jest.advanceTimersByTime(61_000);
        fixture.detectChanges();

        expect(testId('review-expired')).toBeTruthy();
        expect(testId('save-connection')).toBeNull();
      } finally {
        jest.useRealTimers();
      }
    });

    it('saves with the draft id alone and shows Nayax connected only after the save', async () => {
      const { component, connections, fixture, host, runTestWith, testId } = await render({
        save: () => of(state({ status: 'Ready' }))
      });

      runTestWith();
      expect(host.textContent).not.toContain('Nayax connected');

      (testId('save-connection') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(connections.save).toHaveBeenCalledWith('d1f0c2a4-0000-4000-8000-00000000000a');
      expect(component.connection).toEqual(state({ status: 'Ready' }));
      expect(testId('saved')?.textContent).toContain('Nayax connected');
      expect(testId('back')).toBeNull();
      expect(testId('stored-status')?.textContent).toContain('Ready');

      (testId('done') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(component.dialogOpen).toBe(false);
      expect(testId('open-connect')?.textContent).toContain('Replace connection');
    });

    it('records the pending machine checks the save reported and asks for a re-test later', async () => {
      const { component, fixture, host, runTestWith } = await render({
        save: () => of(state({ status: 'Ready', machineChecksPending: true }))
      });

      runTestWith();
      component.saveConnection();
      fixture.detectChanges();

      expect(host.textContent).toContain('Re-test once your machines are set up');
    });

    it('withdraws a refused draft and shows the server refusal, so it is never saved twice', async () => {
      const { component, fixture, runTestWith, testId } = await render({
        save: () =>
          throwError(() => ({
            status: 409,
            error: { detail: 'This test result was already used or has expired. Run the test again.' }
          }))
      });

      runTestWith();
      component.saveConnection();
      fixture.detectChanges();

      expect(testId('dialog-notice')?.textContent).toContain('already used or has expired');
      expect(component.result).toBeNull();
      expect(component.step).toBe(2);
    });

    it('cannot be closed while the save is in flight', async () => {
      const pending = new Subject<NayaxConnectionState>();
      const { component, fixture, runTestWith } = await render({ save: () => pending });

      runTestWith();
      component.saveConnection();
      component.closeDialog();
      fixture.detectChanges();

      expect(component.dialogOpen).toBe(true);
      const dialog = fixture.debugElement.query(By.directive(ModalDialogComponent))
        .componentInstance as ModalDialogComponent;
      expect(dialog.closeDisabled).toBe(true);
    });

    it('drops the result when the Owner goes back, so a fresh test is needed', async () => {
      const { component, fixture, host, runTestWith } = await render();

      runTestWith();
      component.back();
      fixture.detectChanges();

      expect(component.step).toBe(2);
      expect(component.result).toBeNull();
      expect(host.querySelector<HTMLInputElement>('#nayax-token')?.value).toBe('');
    });
  });

  describe('Closing and reopening', () => {
    it('clears the token field and keeps nothing of the attempt', async () => {
      const { component, fixture, host, openDialog, toTokenStep, pasteToken } = await render();

      toTokenStep();
      const field = pasteToken(TOKEN);
      component.closeDialog();
      fixture.detectChanges();

      expect(field.value).toBe('');
      expect(host.querySelector('#nayax-token')).toBeNull();

      openDialog();

      expect(component.step).toBe(1);
      expect(component.operatorId).toBe('');
      expect(component.result).toBeNull();
    });

    it('ignores a test answer that arrives after the dialog was closed', async () => {
      const pending = new Subject<NayaxConnectionTestResult>();
      const { component, fixture, runTestWith, toast } = await render({ test: () => pending });

      runTestWith();
      component.closeDialog();
      fixture.detectChanges();
      pending.next(testResult());
      fixture.detectChanges();

      expect(component.dialogOpen).toBe(false);
      expect(component.result).toBeNull();
      expect(component.testing).toBe(false);
      expect(toast.success).not.toHaveBeenCalled();
    });

    it('drops an unsaved result on close and leaves the stored connection untouched', async () => {
      const { component, connections, fixture, openDialog, runTestWith, testId } = await render({
        status: () => of(state({ status: 'Ready' }))
      });

      runTestWith();
      component.closeDialog();
      fixture.detectChanges();
      openDialog();

      expect(component.result).toBeNull();
      expect(component.step).toBe(1);
      expect(connections.save).not.toHaveBeenCalled();
      expect(testId('stored-status')?.textContent).toContain('Ready');
    });
  });

  describe('The stored connection and its re-test', () => {
    it('reads the stored connection state on arrival', async () => {
      const { connections, host } = await render({ status: () => of(state({ status: 'Ready' })) });

      expect(connections.status).toHaveBeenCalledTimes(1);
      expect(host.textContent).toContain('Ready');
    });

    it('stays usable when the stored state cannot be read', async () => {
      const { testId } = await render({ status: () => throwError(() => ({ status: 500 })) });

      expect(testId('status-unavailable')?.textContent).toContain('could not be read');
      expect((testId('open-connect') as HTMLButtonElement).disabled).toBe(false);
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
              checks: [{ check: 'Machines', status: 'Failed', failure: 'NayaxUnavailable' }]
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
    it('renders no launcher and no action for a member without Integration.Manage', async () => {
      const { component, connections, fixture, host, testId } = await render({}, { canManage: false });

      expect(testId('open-connect')).toBeNull();
      expect(host.textContent).toContain('Owner');

      component.openDialog();
      fixture.detectChanges();
      component.runTest();
      component.saveConnection();
      component.retest();

      expect(component.dialogOpen).toBe(false);
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
      const { component, connections, toTokenStep, pasteToken, testId } = await render(
        {},
        { sessionActive: of(true) }
      );

      toTokenStep();
      pasteToken(TOKEN);

      expect((testId('run-test') as HTMLButtonElement).disabled).toBe(true);

      component.runTest();

      expect(connections.test).not.toHaveBeenCalled();
    });

    it('disables Re-test while a session is active, and retest() writes nothing', async () => {
      const { component, connections, testId } = await render(
        { status: () => of(state({ status: 'Ready' })) },
        { sessionActive: of(true) }
      );

      expect((testId('retest') as HTMLButtonElement).disabled).toBe(true);

      component.retest();

      expect(connections.retest).not.toHaveBeenCalled();
    });

    it('disables Save connection once a session starts after a passing test, and saveConnection() writes nothing', async () => {
      const sessionActive = new Subject<boolean>();
      sessionActive.next(false);
      const { component, connections, fixture, runTestWith, testId } = await render({}, { sessionActive });

      runTestWith();
      expect(component.canSave).toBe(true);

      sessionActive.next(true);
      fixture.detectChanges();

      expect(component.canSave).toBe(false);
      expect((testId('save-connection') as HTMLButtonElement).disabled).toBe(true);

      component.saveConnection();

      expect(connections.save).not.toHaveBeenCalled();
    });
  });
});

/** Serialises a component without following the DOM and framework references it holds. */
function safeJson(): (key: string, value: unknown) => unknown {
  const seen = new WeakSet<object>();
  return (_key, value) => {
    if (typeof value === 'object' && value !== null) {
      if (seen.has(value) || value instanceof Node) {
        return undefined;
      }
      seen.add(value);
    }
    return value;
  };
}
