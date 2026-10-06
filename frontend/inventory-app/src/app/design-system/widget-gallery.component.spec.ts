import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ICON_PATHS } from '../components/shared/icon-paths';
import { ToastService } from '../services/toast.service';
import { WidgetGalleryComponent } from './widget-gallery.component';

/**
 * The gallery is a screenshot fixture, so what matters is that it still renders every widget the
 * #409 series has to show and still drives the real services - not how it looks.
 */
describe('WidgetGalleryComponent', () => {
  async function render() {
    await TestBed.configureTestingModule({ imports: [WidgetGalleryComponent] }).compileComponents();

    const fixture = TestBed.createComponent(WidgetGalleryComponent);
    fixture.detectChanges();

    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('renders every bundled icon so the strip screenshot covers the whole set', async () => {
    const { host } = await render();

    const renderedIcons = Array.from(host.querySelectorAll('[data-icon]')).map((element) => element.getAttribute('data-icon'));

    expect(renderedIcons).toEqual(Object.keys(ICON_PATHS));
    expect(host.querySelectorAll('[data-icon] svg path')).toHaveLength(Object.keys(ICON_PATHS).length);
  });

  it('shows the confirmation dialog only once it has been opened, and hides it again on either choice', async () => {
    const { fixture, host } = await render();

    expect(host.querySelector('app-confirmation-dialog')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="open-confirmation"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('app-confirmation-dialog')).not.toBeNull();

    fixture.componentInstance.closeConfirmation();
    fixture.detectChanges();
    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
  });

  it('raises one real toast of each variant', async () => {
    const { fixture, host } = await render();
    const toastService = TestBed.inject(ToastService);

    for (const variant of fixture.componentInstance.toastVariants) {
      host.querySelector<HTMLButtonElement>(`[data-testid="show-toast-${variant.type}"]`)!.click();
    }

    const messages = await firstValueFrom(toastService.messages$);
    expect(messages.map((toast) => toast.type)).toEqual(['success', 'warning', 'error', 'info']);
  });

  it('renders the multi-select dropdown with neutral sample options', async () => {
    const { fixture, host } = await render();

    expect(host.querySelector('app-multi-select-dropdown')).not.toBeNull();
    expect(fixture.componentInstance.sampleOptions.map((option) => option.label)).toEqual([
      'Sample product A',
      'Sample product B',
      'Sample product C',
      'Sample product D'
    ]);
  });
});
