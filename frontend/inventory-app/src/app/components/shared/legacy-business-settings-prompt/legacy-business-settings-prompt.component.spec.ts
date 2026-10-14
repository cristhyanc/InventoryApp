import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { BusinessSettings, BusinessSettingsService } from '../../../services/business-settings.service';
import { LegacyBusinessSettingsPromptComponent } from './legacy-business-settings-prompt.component';

const COMPLETE: BusinessSettings = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: '51824753556',
  gstRegistered: true,
  timeZoneId: 'Australia/Sydney'
};

const LEGACY_NO_CONTACT: BusinessSettings = { ...COMPLETE, contactEmail: null };
const LEGACY_NO_GST: BusinessSettings = { ...COMPLETE, gstRegistered: null };

interface SettingsStub {
  get: jest.Mock<Observable<BusinessSettings>, []>;
}

async function render(canManage: boolean, settingsSource: Observable<BusinessSettings>) {
  const settings: SettingsStub = { get: jest.fn(() => settingsSource) };

  await TestBed.configureTestingModule({
    imports: [LegacyBusinessSettingsPromptComponent],
    providers: [
      provideRouter([]),
      { provide: BusinessSettingsService, useValue: settings },
      { provide: AccessService, useValue: { allows: () => of(canManage) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(LegacyBusinessSettingsPromptComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;

  return { fixture, host, settings };
}

const prompt = (host: HTMLElement) => host.querySelector('[data-testid="legacy-business-settings-prompt"]');

/**
 * The dismissible "complete your business profile" prompt (issue #549), composed into the
 * Dashboard and the Business Settings page: shown to an Owner alone, and only while the business
 * is missing its contact email or its GST registration - the migrated business that predates the
 * application form.
 */
describe('LegacyBusinessSettingsPromptComponent (issue #549)', () => {
  it('shows the prompt for an Owner whose business is missing its contact email', async () => {
    const { host } = await render(true, of(LEGACY_NO_CONTACT));

    expect(prompt(host)).not.toBeNull();
  });

  it('shows the prompt for an Owner whose business is missing its GST registration', async () => {
    const { host } = await render(true, of(LEGACY_NO_GST));

    expect(prompt(host)).not.toBeNull();
  });

  it('hides the prompt once both the contact email and the GST registration are recorded', async () => {
    const { host } = await render(true, of(COMPLETE));

    expect(prompt(host)).toBeNull();
  });

  it('never fetches the settings, and never shows the prompt, for anybody but an Owner', async () => {
    const { host, settings } = await render(false, of(LEGACY_NO_CONTACT));

    expect(settings.get).not.toHaveBeenCalled();
    expect(prompt(host)).toBeNull();
  });

  it('hides the prompt while the settings have not been read yet', async () => {
    const { host } = await render(true, new Subject<BusinessSettings>().asObservable());

    expect(prompt(host)).toBeNull();
  });

  it('hides the prompt when the settings read fails, rather than guessing the profile is incomplete', async () => {
    const { host, settings } = await render(true, throwError(() => new Error('boom')));

    expect(settings.get).toHaveBeenCalledTimes(1);
    expect(prompt(host)).toBeNull();
  });

  it('links to the Business Settings page', async () => {
    const { host } = await render(true, of(LEGACY_NO_CONTACT));

    const link = host.querySelector<HTMLAnchorElement>('[data-testid="legacy-business-settings-prompt-link"]');
    expect(link?.getAttribute('href')).toBe('/admin/business-settings');
  });

  it('is dismissible, and stays dismissed', async () => {
    const { fixture, host } = await render(true, of(LEGACY_NO_CONTACT));
    expect(prompt(host)).not.toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="legacy-business-settings-prompt-dismiss"]')?.click();
    fixture.detectChanges();

    expect(prompt(host)).toBeNull();
  });
});
