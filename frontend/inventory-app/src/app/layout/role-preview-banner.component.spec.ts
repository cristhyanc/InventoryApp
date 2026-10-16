import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BehaviorSubject } from 'rxjs';
import { PreviewRole } from '../role-preview/role-preview';
import { RolePreviewService } from '../role-preview/role-preview.service';
import { RolePreviewBannerComponent } from './role-preview-banner.component';

interface Rendered {
  readonly fixture: ComponentFixture<RolePreviewBannerComponent>;
  readonly host: HTMLElement;
  readonly role$: BehaviorSubject<PreviewRole | null>;
  readonly exit: jest.Mock;
  banner(): string;
  exitButton(): HTMLButtonElement | null;
}

async function render(role: PreviewRole | null): Promise<Rendered> {
  const role$ = new BehaviorSubject<PreviewRole | null>(role);
  const exit = jest.fn(() => role$.next(null));

  await TestBed.configureTestingModule({
    imports: [RolePreviewBannerComponent],
    providers: [{ provide: RolePreviewService, useValue: { role$, exit } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(RolePreviewBannerComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;

  return {
    fixture,
    host,
    role$,
    exit,
    banner: () => host.querySelector('.role-preview-banner')?.textContent?.replace(/\s+/g, ' ').trim() ?? '',
    exitButton: () => host.querySelector<HTMLButtonElement>('.role-preview-banner button')
  };
}

describe('RolePreviewBannerComponent (issue #721)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('names the previewed role and says it is read-only', async () => {
    const rendered = await render('Operator');

    expect(rendered.banner()).toContain('Viewing as Operator (preview, read-only)');
    expect(rendered.exitButton()?.textContent?.trim()).toBe('Exit preview');
    expect(rendered.host.querySelector('[role="status"]')).not.toBeNull();
  });

  it('shows nothing at all while no preview is active', async () => {
    const rendered = await render(null);

    expect(rendered.banner()).toBe('');
  });

  it('asks the service to exit, and disappears when it has', async () => {
    const rendered = await render('Manager');

    rendered.exitButton()?.click();
    rendered.fixture.detectChanges();

    expect(rendered.exit).toHaveBeenCalledTimes(1);
    expect(rendered.banner()).toBe('');
  });

  it('follows the role the service publishes', async () => {
    const rendered = await render(null);

    rendered.role$.next('Owner');
    rendered.fixture.detectChanges();

    expect(rendered.banner()).toContain('Viewing as Owner (preview, read-only)');
  });
});
