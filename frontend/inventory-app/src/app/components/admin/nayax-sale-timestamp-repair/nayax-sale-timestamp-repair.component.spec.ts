import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { NayaxSaleTimestampRepairComponent } from './nayax-sale-timestamp-repair.component';
import { NayaxSaleTimestampRepairWorkflowComponent } from './nayax-sale-timestamp-repair-workflow.component';
import { NayaxSaleTimestampRepairService } from '../../../services/nayax-sale-timestamp-repair.service';
import { ToastService } from '../../../services/toast.service';

async function render() {
  const previewFn = jest.fn(() => of({}));
  const applyFn = jest.fn(() => of({}));
  await TestBed.configureTestingModule({
    imports: [NayaxSaleTimestampRepairComponent],
    providers: [
      provideRouter([]),
      { provide: NayaxSaleTimestampRepairService, useValue: { preview: previewFn, apply: applyFn } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxSaleTimestampRepairComponent);
  fixture.detectChanges();
  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    previewFn,
    applyFn,
    workflow: fixture.debugElement.query(By.directive(NayaxSaleTimestampRepairWorkflowComponent))
      ?.componentInstance as NayaxSaleTimestampRepairWorkflowComponent
  };
}

/**
 * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
 * #191): it renders the heading and hosts `NayaxSaleTimestampRepairWorkflowComponent`, which owns
 * the sources, the Preview/Apply lifecycle and the confirmation and is tested in its own spec.
 */
describe('NayaxSaleTimestampRepairComponent page composition (issues #487, #191)', () => {
  it('composes the workflow component instead of owning the workflow itself', async () => {
    const { host, workflow } = await render();

    expect(workflow).toBeTruthy();
    expect(host.textContent).toContain('Nayax Sale Timestamp Repair');
  });

  /**
   * Nothing is read, previewed or written on arrival: this maintenance action only ever runs
   * because a person pressed Preview and then confirmed Apply, never on navigation.
   */
  it('previews and applies nothing on arrival', async () => {
    const { previewFn, applyFn } = await render();

    expect(previewFn).not.toHaveBeenCalled();
    expect(applyFn).not.toHaveBeenCalled();
  });

  it('links back to the Admin hub and says nothing is written until the operator confirms', async () => {
    const { host } = await render();

    expect(host.querySelector('a')?.getAttribute('href')).toBe('/admin');
    expect(host.textContent?.replace(/\s+/g, ' ')).toContain('nothing is written until you do');
  });

  /** A routed page must not author a workflow's own dialog markup (page-composition.guard.ts). */
  it('authors no dialog markup of its own', async () => {
    const { host } = await render();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });
});
