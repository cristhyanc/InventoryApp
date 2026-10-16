import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, of, throwError } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { CAPABILITIES, capabilitySet } from '../access/capabilities';
import { PreviewRole } from '../role-preview/role-preview';
import { RolePreviewService } from '../role-preview/role-preview.service';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { RolePreviewMenuComponent, offeredPreviewRoles } from './role-preview-menu.component';

function granted(role: string | null): MemberAccessState {
  return {
    status: 'granted',
    access: {
      role,
      capabilities: capabilitySet([...CAPABILITIES]),
      businessName: 'Vending Co',
      timeZoneId: 'Australia/Sydney'
    }
  };
}

const UNAVAILABLE: MemberAccessState = { status: 'unavailable' };
const UNREAD: MemberAccessState = { status: 'unread' };

interface Rendered {
  readonly fixture: ComponentFixture<RolePreviewMenuComponent>;
  /** The labels of the roles the menu offers, in order. */
  roles(): string[];
  readonly start: jest.Mock;
  readonly started: PreviewRole[];
  readonly previewing: BehaviorSubject<PreviewRole | null>;
}

async function render(
  options: {
    platformAdmin?: Observable<boolean>;
    access?: MemberAccessState;
    previewing?: PreviewRole | null;
  } = {}
): Promise<Rendered> {
  const previewing = new BehaviorSubject<PreviewRole | null>(options.previewing ?? null);
  const start = jest.fn((role: PreviewRole) => previewing.next(role));

  await TestBed.configureTestingModule({
    imports: [RolePreviewMenuComponent],
    providers: [
      { provide: AccessService, useValue: { access$: of(options.access ?? granted('Owner')) } },
      { provide: PlatformDiagnosticsAccessService, useValue: { granted$: options.platformAdmin ?? of(true) } },
      { provide: RolePreviewService, useValue: { role$: previewing, start } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(RolePreviewMenuComponent);
  const started: PreviewRole[] = [];
  fixture.componentInstance.previewStarted.subscribe((role) => started.push(role));
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  return {
    fixture,
    roles: () =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('[role="group"] button')).map(
        (button) => button.textContent?.trim() ?? ''
      ),
    start,
    started,
    previewing
  };
}

/** The **View as** entry (issue #721): who is offered it, and which roles it offers them. */
describe('RolePreviewMenuComponent (issue #721)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('offers a platform administrator every role but their own', async () => {
    const rendered = await render({ access: granted('Owner') });

    expect(rendered.roles()).toEqual(['Manager', 'Operator']);
  });

  it('offers all three roles inside a support session, where the API reports no role', async () => {
    const rendered = await render({ access: granted(null) });

    expect(rendered.roles()).toEqual(['Owner', 'Manager', 'Operator']);
  });

  it('offers nothing to somebody the platform-admin probe refuses', async () => {
    const rendered = await render({ platformAdmin: of(false) });

    expect(rendered.roles()).toEqual([]);
    expect((rendered.fixture.nativeElement as HTMLElement).textContent).not.toContain('View as');
  });

  it('offers nothing while the probe has not answered, and nothing when it fails', async () => {
    expect((await render({ platformAdmin: new BehaviorSubject<boolean>(false) })).roles()).toEqual([]);

    TestBed.resetTestingModule();

    expect((await render({ platformAdmin: throwError(() => new Error('probe failed')) })).roles()).toEqual([]);
  });

  it.each([
    ['unavailable', UNAVAILABLE],
    ['unread', UNREAD]
  ])('offers nothing while the capability answer is %s', async (_case, access) => {
    const rendered = await render({ access });

    expect(rendered.roles()).toEqual([]);
  });

  it('offers nothing while a preview is already active', async () => {
    const rendered = await render({ previewing: 'Operator' });

    expect(rendered.roles()).toEqual([]);
  });

  it('starts the preview, reports it, and stops offering the entry', async () => {
    const rendered = await render({ access: granted('Owner') });

    const manager = (rendered.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>(
      '[role="group"] button'
    )[0];
    manager.click();
    rendered.fixture.detectChanges();

    expect(rendered.start).toHaveBeenCalledWith('Manager');
    expect(rendered.started).toEqual(['Manager']);
    expect(rendered.roles()).toEqual([]);
  });

  it('names the group of roles for a screen reader', async () => {
    const rendered = await render();
    const host = rendered.fixture.nativeElement as HTMLElement;

    const group = host.querySelector('[role="group"]');
    const heading = host.querySelector(`#${group?.getAttribute('aria-labelledby')}`);

    expect(heading?.textContent?.trim()).toBe('View as');
  });

  /** The rule itself, which the rendering above only shows one view of. */
  describe('offeredPreviewRoles', () => {
    it.each([
      ['Owner', ['Manager', 'Operator']],
      ['Manager', ['Owner', 'Operator']],
      ['Operator', ['Owner', 'Manager']]
    ])('excludes the caller’s own %s role', (own, expected) => {
      expect(offeredPreviewRoles(true, granted(own), null)).toEqual(expected);
    });

    it('offers nothing to anybody the platform-admin probe has not granted', () => {
      expect(offeredPreviewRoles(false, granted('Owner'), null)).toEqual([]);
    });

    it('offers nothing while a preview is active, whatever else is true', () => {
      expect(offeredPreviewRoles(true, granted(null), 'Manager')).toEqual([]);
    });

    /** A role name the API reports that this build does not know is simply not one of the three. */
    it('offers all three for an unrecognised role name', () => {
      expect(offeredPreviewRoles(true, granted('Auditor'), null)).toEqual(['Owner', 'Manager', 'Operator']);
    });
  });
});
