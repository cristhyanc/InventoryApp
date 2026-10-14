import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { BusinessService } from '../../../services/business.service';
import { BusinessSettings, BusinessSettingsService } from '../../../services/business-settings.service';
import { LegacyBusinessSettingsPromptComponent } from '../../shared/legacy-business-settings-prompt/legacy-business-settings-prompt.component';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { BusinessSettingsFormComponent } from './business-settings-form.component';
import { BusinessSettingsComponent, LOAD_ERROR_MESSAGE } from './business-settings.component';

@Component({ standalone: true, template: 'admin' })
class AdminPageComponent {}

const SETTINGS: BusinessSettings = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: '51824753556',
  gstRegistered: true,
  timeZoneId: 'Australia/Sydney'
};

interface Harness {
  fixture: ComponentFixture<BusinessSettingsComponent>;
  host: HTMLElement;
  get: jest.Mock;
}

async function render(settings$?: Observable<BusinessSettings>): Promise<Harness> {
  const get = jest.fn(() => settings$ ?? of(SETTINGS));

  await TestBed.configureTestingModule({
    imports: [BusinessSettingsComponent],
    providers: [
      provideRouter([{ path: 'admin', component: AdminPageComponent }]),
      { provide: BusinessSettingsService, useValue: { get, save: jest.fn() } },
      { provide: BusinessService, useValue: { refresh: jest.fn(() => of(null)) } },
      { provide: AccessService, useValue: { refresh: jest.fn(), allows: () => of(false) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessSettingsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, get };
}

function byTestId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector(`[data-testid="${id}"]`);
}

function form(harness: Harness): BusinessSettingsFormComponent | null {
  return harness.fixture.debugElement.query(By.directive(BusinessSettingsFormComponent))?.componentInstance ?? null;
}

/**
 * The Admin → Business Settings page (issue #549): a composition boundary per
 * docs/architecture.md § Page composition boundary. It reads the settings once and composes the
 * legacy-profile prompt and `BusinessSettingsFormComponent`; the edit workflow's own behaviour is
 * covered by `business-settings-form.component.spec.ts`.
 */
describe('BusinessSettingsComponent (issue #549)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('reads the settings once and composes the form with them', async () => {
    const harness = await render();

    expect(harness.get).toHaveBeenCalledTimes(1);
    expect(form(harness)?.draft).toEqual({
      name: 'Corner Store',
      contactEmail: 'owner@example.com',
      abn: '51824753556',
      gstRegistered: true,
      timeZoneId: 'Australia/Sydney'
    });
  });

  it('composes the legacy business profile prompt', async () => {
    const harness = await render();

    expect(harness.fixture.debugElement.query(By.directive(LegacyBusinessSettingsPromptComponent))).not.toBeNull();
  });

  it('composes no form while the settings are still being read', async () => {
    const harness = await render(new Subject<BusinessSettings>());

    expect(byTestId(harness.host, 'business-settings-loading')).not.toBeNull();
    expect(form(harness)).toBeNull();
  });

  it('shows a load error and composes no form when the settings cannot be read', async () => {
    const harness = await render(throwError(() => new Error('network error')));

    expect(byTestId(harness.host, 'business-settings-load-error')?.textContent).toContain(LOAD_ERROR_MESSAGE);
    expect(form(harness)).toBeNull();
    expect(harness.host.querySelector('form')).toBeNull();
  });
});
