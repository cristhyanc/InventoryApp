import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ToastContainerComponent } from './toast-container.component';
import { ToastService } from '../../services/toast.service';

describe('ToastContainerComponent', () => {
  let fixture: ComponentFixture<ToastContainerComponent>;
  let service: ToastService;
  let host: HTMLElement;

  beforeEach(async () => {
    jest.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [ToastContainerComponent] }).compileComponents();
    service = TestBed.inject(ToastService);
    fixture = TestBed.createComponent(ToastContainerComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    jest.clearAllTimers();
    jest.useRealTimers();
    TestBed.resetTestingModule();
  });

  const toasts = () => Array.from(host.querySelectorAll<HTMLElement>('.toast'));
  const closeButtons = () => Array.from(host.querySelectorAll<HTMLButtonElement>('.toast-close'));

  it('gives every toast a native, named close button with a hidden glyph', () => {
    service.success('Saved');
    service.error('Failed');
    fixture.detectChanges();

    const buttons = closeButtons();
    expect(buttons).toHaveLength(2);
    for (const button of buttons) {
      expect(button.tagName).toBe('BUTTON');
      expect(button.getAttribute('type')).toBe('button');
      expect(button.getAttribute('aria-label')).toBe('Dismiss notification');
      const glyph = button.querySelector('[aria-hidden="true"]');
      expect(glyph?.textContent?.trim()).toBe('×');
    }
  });

  it('removes only the toast whose close button is activated', () => {
    service.success('Saved');
    service.error('Failed');
    fixture.detectChanges();

    closeButtons()[0].click();
    fixture.detectChanges();

    expect(toasts().map((t) => t.querySelector('.toast-body')?.textContent?.trim())).toEqual(['Failed']);
  });

  it('keeps the existing text, title and severity classes', () => {
    service.error('Failed', 'Oops');
    service.warning('Careful');
    fixture.detectChanges();

    const [error, warning] = toasts();
    expect(error.classList).toContain('alert-danger');
    expect(error.querySelector('.alert-title')?.textContent?.trim()).toBe('Oops');
    expect(error.querySelector('.toast-body')?.textContent?.trim()).toBe('Failed');
    expect(warning.classList).toContain('alert-warning');
  });

  it('still shows a toast at 4,999 ms and removes it at 5,000 ms', () => {
    service.info('Heads up');
    fixture.detectChanges();

    jest.advanceTimersByTime(4999);
    fixture.detectChanges();
    expect(toasts()).toHaveLength(1);

    jest.advanceTimersByTime(1);
    fixture.detectChanges();
    expect(toasts()).toHaveLength(0);
  });
});
