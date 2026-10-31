import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ABN_INVALID_MESSAGE, NAME_REQUIRED_MESSAGE } from '../../../access/business-application-rules';
import { BusinessService, CurrentBusiness } from '../../../services/business.service';
import { BusinessSettings, BusinessSettingsService } from '../../../services/business-settings.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import {
  BusinessSettingsFormComponent,
  GENERIC_SAVE_ERROR_MESSAGE,
  SAVED_BUT_NOT_REFRESHED_MESSAGE,
  TIME_ZONE_CHANGE_CONFIRMATION_MESSAGE
} from './business-settings-form.component';

const SETTINGS: BusinessSettings = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: '51824753556',
  gstRegistered: true,
  timeZoneId: 'Australia/Sydney',
  state: 'NSW'
};

const LEGACY_SETTINGS: BusinessSettings = {
  ...SETTINGS,
  contactEmail: null,
  gstRegistered: null,
  abn: null,
  state: null
};

/** What a successful `BusinessService.refresh()` emits; `null` is its "could not be read". */
const CURRENT_BUSINESS: CurrentBusiness = { name: 'Corner Store', timeZoneId: 'Australia/Brisbane' };

interface Harness {
  fixture: ComponentFixture<BusinessSettingsFormComponent>;
  host: HTMLElement;
  save: jest.Mock;
  businessRefresh: jest.Mock;
  accessRefresh: jest.Mock;
}

async function render(
  options: { settings?: BusinessSettings; save?: jest.Mock; sessionActive?: Observable<boolean> }
): Promise<Harness> {
  const save = options.save ?? jest.fn(() => of({ ...SETTINGS, timeZoneId: 'Australia/Brisbane' }));
  const businessRefresh = jest.fn((): Observable<CurrentBusiness | null> => of(CURRENT_BUSINESS));
  const accessRefresh = jest.fn(() => of({ status: 'unread' as const }));

  await TestBed.configureTestingModule({
    imports: [BusinessSettingsFormComponent],
    providers: [
      { provide: BusinessSettingsService, useValue: { get: jest.fn(() => of(SETTINGS)), save } },
      { provide: BusinessService, useValue: { refresh: businessRefresh } },
      { provide: AccessService, useValue: { refresh: accessRefresh, allows: () => of(false) } },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessSettingsFormComponent);
  fixture.componentRef.setInput('settings', options.settings ?? SETTINGS);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, save, businessRefresh, accessRefresh };
}

function byTestId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector(`[data-testid="${id}"]`);
}

function refusal(status: number, body: unknown): Observable<never> {
  return throwError(() => new HttpErrorResponse({ status, error: body }));
}

async function submitForm(harness: Harness): Promise<void> {
  harness.fixture.componentInstance.submit();
  harness.fixture.detectChanges();
  await harness.fixture.whenStable();
  harness.fixture.detectChanges();
}

/**
 * The Business Settings edit workflow (issue #549), the dedicated feature component the page
 * composes: it edits the six settings issues #548 and #933 added, validates and saves them with the
 * application form's own rules, confirms before a time zone change, and refreshes the shell
 * before reporting success.
 */
describe('BusinessSettingsFormComponent (issue #549)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('loads the settings it is given into the form', async () => {
    const { fixture } = await render({});

    expect(fixture.componentInstance.draft).toEqual({
      name: 'Corner Store',
      contactEmail: 'owner@example.com',
      abn: '51824753556',
      gstRegistered: true,
      timeZoneId: 'Australia/Sydney',
      state: 'NSW'
    });
  });

  it("shows the legacy business's missing fields as blank and unstated, never as a false default", async () => {
    const { fixture } = await render({ settings: LEGACY_SETTINGS });

    expect(fixture.componentInstance.draft).toEqual({
      name: 'Corner Store',
      contactEmail: '',
      abn: '',
      gstRegistered: null,
      timeZoneId: 'Australia/Sydney',
      // Unchosen rather than guessed at, so the field asks for it (issue #933).
      state: ''
    });
  });

  it('shows no field error until the field is left', async () => {
    const { fixture, host } = await render({});

    expect(byTestId(host, 'error-name')).toBeNull();

    const name = host.querySelector('#businessName') as HTMLInputElement;
    name.value = '';
    name.dispatchEvent(new Event('input'));
    name.dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(byTestId(host, 'error-name')?.textContent).toContain(NAME_REQUIRED_MESSAGE);
  });

  it('shows every field error and saves nothing when an invalid form is submitted', async () => {
    const harness = await render({});
    harness.fixture.componentInstance.draft.name = '';
    harness.fixture.componentInstance.draft.abn = '123';

    await submitForm(harness);

    expect(harness.save).not.toHaveBeenCalled();
    expect(byTestId(harness.host, 'error-name')?.textContent).toContain(NAME_REQUIRED_MESSAGE);
    expect(byTestId(harness.host, 'error-abn')?.textContent).toContain(ABN_INVALID_MESSAGE);
  });

  it('saves immediately, with no confirmation, when the time zone is unchanged', async () => {
    const save = jest.fn(() => of(SETTINGS));
    const harness = await render({ save });

    await submitForm(harness);

    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
    expect(save).toHaveBeenCalledWith({
      name: 'Corner Store',
      contactEmail: 'owner@example.com',
      abn: '51824753556',
      gstRegistered: true,
      timeZoneId: 'Australia/Sydney',
      state: 'NSW'
    });
  });

  /**
   * Issue #933: the state is a field of this form, it is submitted with the rest of the set, and a
   * business that has recorded none is refused before anything is sent - the same shape as the
   * contact address and the GST registration.
   */
  it('saves a changed state with the rest of the settings', async () => {
    const save = jest.fn(() => of({ ...SETTINGS, state: 'ACT' }));
    const harness = await render({ save });
    harness.fixture.componentInstance.draft.state = 'ACT';

    await submitForm(harness);

    expect(save).toHaveBeenCalledWith(expect.objectContaining({ state: 'ACT', timeZoneId: 'Australia/Sydney' }));
  });

  it('saves nothing while the state of a business that recorded none is unchosen', async () => {
    const harness = await render({ settings: LEGACY_SETTINGS });
    harness.fixture.componentInstance.draft.contactEmail = 'owner@example.com';
    harness.fixture.componentInstance.draft.gstRegistered = true;

    await submitForm(harness);

    expect(harness.save).not.toHaveBeenCalled();
    expect(byTestId(harness.host, 'error-state')).not.toBeNull();
  });

  it('offers the eight states and territories, and nothing else', async () => {
    const { host } = await render({});

    const options = Array.from(host.querySelectorAll<HTMLOptionElement>('#businessState option')).map(
      (option) => option.value
    );

    expect(options).toEqual(['', 'NSW', 'VIC', 'QLD', 'SA', 'WA', 'TAS', 'ACT', 'NT']);
  });

  it('asks for confirmation before saving a changed time zone, and saves nothing until confirmed', async () => {
    const harness = await render({});
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';

    await submitForm(harness);

    expect(harness.save).not.toHaveBeenCalled();
    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(true);
    expect(harness.host.textContent).toContain(TIME_ZONE_CHANGE_CONFIRMATION_MESSAGE);
  });

  it('saves the new time zone once the confirmation is accepted', async () => {
    const harness = await render({});
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';
    await submitForm(harness);

    harness.fixture.componentInstance.confirmTimeZoneChange();
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();
    harness.fixture.detectChanges();

    expect(harness.save).toHaveBeenCalledWith(
      expect.objectContaining({ timeZoneId: 'Australia/Brisbane' })
    );
    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
  });

  it('saves nothing when the time zone confirmation is cancelled, leaving the typed value on screen', async () => {
    const harness = await render({});
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';
    await submitForm(harness);

    harness.fixture.componentInstance.cancelTimeZoneChange();

    expect(harness.save).not.toHaveBeenCalled();
    expect(harness.fixture.componentInstance.draft.timeZoneId).toBe('Australia/Brisbane');
    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
  });

  it("refreshes BusinessService and AccessService before reporting success, and shows success only after both answer", async () => {
    const order: string[] = [];
    const save = jest.fn(() => {
      order.push('save');
      return of({ ...SETTINGS, timeZoneId: 'Australia/Brisbane' });
    });
    const harness = await render({ save });
    harness.businessRefresh.mockImplementation(() => {
      order.push('business-refresh');
      return of(CURRENT_BUSINESS);
    });
    harness.accessRefresh.mockImplementation(() => {
      order.push('access-refresh');
      return of({ status: 'unread' as const });
    });

    expect(byTestId(harness.host, 'save-success')).toBeNull();

    await submitForm(harness);

    expect(order).toEqual(['save', 'business-refresh', 'access-refresh']);
    expect(harness.businessRefresh).toHaveBeenCalledTimes(1);
    expect(harness.accessRefresh).toHaveBeenCalledTimes(1);
    expect(byTestId(harness.host, 'save-success')?.textContent).toContain('Business settings saved.');
  });

  it('does not refresh the shell or report success when the save is refused', async () => {
    const harness = await render({ save: jest.fn(() => refusal(400, { message: 'An ABN must be 11 digits and pass the ABN check-digit test.' })) });

    await submitForm(harness);

    expect(harness.businessRefresh).not.toHaveBeenCalled();
    expect(harness.accessRefresh).not.toHaveBeenCalled();
    expect(byTestId(harness.host, 'submit-error')?.textContent).toContain(
      'An ABN must be 11 digits and pass the ABN check-digit test.'
    );
    expect(byTestId(harness.host, 'save-success')).toBeNull();
    expect(harness.fixture.componentInstance.saving).toBe(false);
  });

  it.each<[string, (harness: Harness) => void]>([
    ['the capability read fails', (harness) => harness.accessRefresh.mockReturnValue(of({ status: 'unavailable' as const }))],
    ['the current business read fails', (harness) => harness.businessRefresh.mockReturnValue(of(null))],
    ['a refresh errors', (harness) => harness.accessRefresh.mockReturnValue(throwError(() => new Error('boom')))]
  ])('reports a saved-but-not-refreshed message, not a save failure or success, when %s after the save (issue #681)', async (_label, failRefresh) => {
    const harness = await render({});
    failRefresh(harness);

    await submitForm(harness);

    expect(byTestId(harness.host, 'refresh-error')?.textContent).toContain(SAVED_BUT_NOT_REFRESHED_MESSAGE);
    expect(byTestId(harness.host, 'submit-error')).toBeNull();
    expect(byTestId(harness.host, 'save-success')).toBeNull();
    expect(harness.fixture.componentInstance.saving).toBe(false);
  });

  it('takes the saved settings as the new baseline even when the refresh after the save fails (issue #681)', async () => {
    const harness = await render({});
    harness.accessRefresh.mockReturnValue(throwError(() => new Error('boom')));
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';
    await submitForm(harness);
    harness.fixture.componentInstance.confirmTimeZoneChange();
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    expect(harness.fixture.componentInstance.draft.timeZoneId).toBe('Australia/Brisbane');

    await submitForm(harness);

    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
    expect(harness.save).toHaveBeenCalledTimes(2);
  });

  it('shows a generic sentence for a refusal with no usable message', async () => {
    const harness = await render({ save: jest.fn(() => refusal(500, { message: 'Internal detail' })) });

    await submitForm(harness);

    expect(byTestId(harness.host, 'submit-error')?.textContent).toContain(GENERIC_SAVE_ERROR_MESSAGE);
  });

  it('re-reads the saved settings as the new baseline, so saving again with the same zone needs no second confirmation', async () => {
    const save = jest.fn(() => of({ ...SETTINGS, timeZoneId: 'Australia/Brisbane' }));
    const harness = await render({ save });
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';
    await submitForm(harness);
    harness.fixture.componentInstance.confirmTimeZoneChange();
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    await submitForm(harness);

    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
    expect(save).toHaveBeenCalledTimes(2);
  });
});

/**
 * Save while a support session is active (issue #660). A session grants `BusinessManage`, so this
 * control would otherwise still render enabled.
 */
describe('BusinessSettingsFormComponent write control during a support session (issue #660)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('disables Save while a session is active', async () => {
    const harness = await render({ sessionActive: of(true) });

    expect((harness.host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('does not submit or save while a session is active, even if submit() is invoked directly', async () => {
    const harness = await render({ sessionActive: of(true) });

    await submitForm(harness);

    expect(harness.save).not.toHaveBeenCalled();
    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(false);
  });

  it('does not save a time-zone change confirmed after a session started, even though it was requested before', async () => {
    const sessionActive = new Subject<boolean>();
    sessionActive.next(false);
    const harness = await render({ sessionActive });
    harness.fixture.componentInstance.draft.timeZoneId = 'Australia/Brisbane';
    await submitForm(harness);
    expect(harness.fixture.componentInstance.pendingTimeZoneChange).toBe(true);

    sessionActive.next(true);
    harness.fixture.componentInstance.confirmTimeZoneChange();

    expect(harness.save).not.toHaveBeenCalled();
  });

  it('leaves Save enabled while no session is active', async () => {
    const harness = await render({ sessionActive: of(false) });

    expect((harness.host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(false);
  });
});
