import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { PlatformDiagnosticsAccess, PlatformDiagnosticsService } from '../../../services/platform-diagnostics.service';
import { PlatformDiagnosticsComponent } from './platform-diagnostics.component';
import { PlatformDiagnosticsQueryComponent } from './platform-diagnostics-query.component';

const authorized: PlatformDiagnosticsAccess = {
  authorized: true,
  crossBusinessScope: true,
  limits: { maxSqlBytes: 16384, maxRows: 500, maxResponseBytes: 1048576, maxDurationSeconds: 5 }
};

function httpError(status: number): unknown {
  return { status, error: null };
}

interface Rendered {
  fixture: ComponentFixture<PlatformDiagnosticsComponent>;
  host: HTMLElement;
  accessFn: jest.Mock;
  queryFn: jest.Mock;
  workflow?: PlatformDiagnosticsQueryComponent;
}

async function render(access: Observable<PlatformDiagnosticsAccess> = of(authorized)): Promise<Rendered> {
  const accessFn = jest.fn(() => access);
  const queryFn = jest.fn(() => of({}));

  await TestBed.configureTestingModule({
    imports: [PlatformDiagnosticsComponent],
    providers: [
      provideRouter([]),
      { provide: PlatformDiagnosticsService, useValue: { access: accessFn, query: queryFn } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(PlatformDiagnosticsComponent);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    accessFn,
    queryFn,
    workflow: fixture.debugElement.query(By.directive(PlatformDiagnosticsQueryComponent))
      ?.componentInstance as PlatformDiagnosticsQueryComponent | undefined
  };
}

function testId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
}

/**
 * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
 * #191): it resolves the capability, states the scope, and hosts the query workflow, which owns
 * the statement and every outcome and is tested in its own spec.
 */
describe('PlatformDiagnosticsComponent (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('says the scope is cross-business and that the page is read-only, before anything is asked', async () => {
    const { host } = await render();
    const warning = testId(host, 'diagnostics-scope-warning');

    expect(warning?.textContent).toContain('every business');
    expect(warning?.textContent).toContain('read-only');
    expect(warning?.textContent).toMatch(/cannot write, delete, change the schema or repair/);
    expect(warning?.textContent).toContain('audited');
  });

  it('composes the query workflow with the limits the API published, once access is confirmed', async () => {
    const { host, workflow } = await render();

    expect(workflow).toBeTruthy();
    expect(workflow?.limits).toEqual(authorized.limits);
    expect(testId(host, 'diagnostics-access-denied')).toBeNull();
  });

  /**
   * The acceptance criterion the whole design rests on: the page is linked only for a confirmed
   * platform administrator, and a direct URL is safe because the API refuses independently.
   */
  it('offers no query form at all when the API refuses the capability probe', async () => {
    const { host, workflow } = await render(throwError(() => httpError(403)));

    expect(workflow).toBeUndefined();
    expect(host.querySelector('textarea')).toBeNull();
    const denied = testId(host, 'diagnostics-access-denied');
    expect(denied?.textContent).toContain('not authorized');
    expect(denied?.textContent).toMatch(/separately configured identity/);
    expect(denied?.textContent).toMatch(/granted from this application/);
  });

  it('treats an unauthenticated refusal the same way, rather than showing a form', async () => {
    const { host, workflow } = await render(throwError(() => httpError(401)));

    expect(workflow).toBeUndefined();
    expect(testId(host, 'diagnostics-access-denied')).not.toBeNull();
  });

  /** A 200 always confirms access; a body that says otherwise is refused, never trusted. */
  it('refuses a success body that does not confirm access', async () => {
    const { host, workflow } = await render(of({ ...authorized, authorized: false }));

    expect(workflow).toBeUndefined();
    expect(testId(host, 'diagnostics-access-denied')).not.toBeNull();
  });

  it('reports an unavailable API as unavailable instead of as a refusal or as access', async () => {
    const { host, workflow } = await render(throwError(() => new Error('network down')));

    expect(workflow).toBeUndefined();
    expect(testId(host, 'diagnostics-access-denied')).toBeNull();
    expect(testId(host, 'diagnostics-access-error')?.textContent).toContain('unavailable');
  });

  it('shows a checking state while the probe is outstanding, and no query form yet', async () => {
    const { host, workflow } = await render(new Observable<PlatformDiagnosticsAccess>());

    expect(workflow).toBeUndefined();
    expect(host.querySelector('textarea')).toBeNull();
    expect(testId(host, 'diagnostics-access-loading')?.textContent).toContain('Checking platform-admin access');
  });

  it('runs no query of its own on arrival', async () => {
    const { accessFn, queryFn } = await render();

    expect(accessFn).toHaveBeenCalledTimes(1);
    expect(queryFn).not.toHaveBeenCalled();
  });

  it('links back to the Admin hub', async () => {
    const { host } = await render();

    expect(host.querySelector('a')?.getAttribute('href')).toBe('/admin');
  });
});
