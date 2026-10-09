import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HistoricalGstClassificationComponent } from './historical-gst-classification.component';
import { HistoricalGstClassificationWorkflowComponent } from './historical-gst-classification-workflow.component';
import { HistoricalGstClassificationService } from '../../../services/historical-gst-classification.service';
import { ToastService } from '../../../services/toast.service';

async function render() {
  const previewFn = jest.fn(() => of({ summary: null, fingerprint: '' }));
  await TestBed.configureTestingModule({
    imports: [HistoricalGstClassificationComponent],
    providers: [
      provideRouter([]),
      { provide: HistoricalGstClassificationService, useValue: { preview: previewFn, apply: jest.fn() } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), info: jest.fn() } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalGstClassificationComponent);
  fixture.detectChanges();
  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    previewFn,
    workflow: fixture.debugElement.query(By.directive(HistoricalGstClassificationWorkflowComponent))
      ?.componentInstance as HistoricalGstClassificationWorkflowComponent
  };
}

/**
 * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
 * #191): it renders the heading and hosts `HistoricalGstClassificationWorkflowComponent`, which
 * owns the Preview/Apply workflow and is tested in its own spec.
 */
describe('HistoricalGstClassificationComponent page composition (issues #433, #191)', () => {
  it('composes the workflow component instead of owning the workflow itself', async () => {
    const { host, workflow } = await render();

    expect(workflow).toBeTruthy();
    expect(host.textContent).toContain('Historical GST Classification');
  });

  /**
   * Nothing is read, previewed or written on arrival: this maintenance action only ever runs
   * because a person pressed Preview and then Apply, never on navigation.
   */
  it('previews nothing on arrival', async () => {
    const { previewFn } = await render();

    expect(previewFn).not.toHaveBeenCalled();
  });

  it('links back to the Admin hub and says nothing is written until Apply', async () => {
    const { host } = await render();

    expect(host.querySelector('a')?.getAttribute('href')).toBe('/admin');
    expect(host.textContent).toContain('nothing is written until you do');
  });
});
