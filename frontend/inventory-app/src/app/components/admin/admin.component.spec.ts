import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminComponent } from './admin.component';

async function render() {
  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('AdminComponent Admin split (issue #388)', () => {
  it('links to the dedicated Nayax Settings and Site Commission Agreements pages instead of embedding their forms', async () => {
    const { host } = await render();

    const nayaxLink = host.querySelector('a[routerLink="/admin/nayax-settings"]');
    const commissionLink = host.querySelector('a[routerLink="/admin/site-commission-agreements"]');
    expect(nayaxLink).not.toBeNull();
    expect(commissionLink).not.toBeNull();
    expect(host.querySelector('input[type="date"]')).toBeNull();
  });
});

describe('AdminComponent Admin split (issue #389)', () => {
  it('links to the dedicated Imports page instead of hosting the import actions', async () => {
    const { host } = await render();

    expect(host.querySelector('a[routerLink="/admin/imports"]')).not.toBeNull();
    expect(host.querySelector('input[type="file"]')).toBeNull();
    const buttonText = Array.from(host.querySelectorAll('button')).map(button => button.textContent ?? '');
    expect(buttonText.some(text => text.includes('Import products'))).toBe(false);
    expect(buttonText.some(text => text.includes('Import XML files'))).toBe(false);
    expect(buttonText.some(text => text.includes('Download template'))).toBe(false);
  });
});

describe('AdminComponent Historical GST Classification (issue #433)', () => {
  it('links to the dedicated Historical GST Classification page', async () => {
    const { host } = await render();

    expect(host.querySelector('a[routerLink="/admin/historical-gst-classification"]')).not.toBeNull();
  });
});

describe('AdminComponent Nayax Sale Timestamp Repair (issue #487)', () => {
  it('links to the dedicated Nayax Sale Timestamp Repair page', async () => {
    const { host } = await render();

    expect(host.querySelector('a[routerLink="/admin/nayax-sale-timestamp-repair"]')).not.toBeNull();
  });
});

describe('AdminComponent Admin split (issue #390)', () => {
  it('links to the dedicated Historical Cost Recovery, AVCO Transition and Costing Repair pages', async () => {
    const { host } = await render();

    expect(host.querySelector('a[routerLink="/admin/historical-cost-recovery"]')).not.toBeNull();
    expect(host.querySelector('a[routerLink="/admin/avco-transition"]')).not.toBeNull();
    expect(host.querySelector('a[routerLink="/admin/costing-repair"]')).not.toBeNull();
  });

  it('keeps no second copy of the moved maintenance tools: no action, form control or child workflow remains', async () => {
    const { host } = await render();

    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(host.querySelectorAll('input, select, textarea')).toHaveLength(0);
    expect(host.querySelector('app-costing-repair')).toBeNull();
    const text = host.textContent ?? '';
    expect(text).not.toContain('Dry Run');
    expect(text).not.toContain('Apply Nayax Cost Backfill');
    expect(text).not.toContain('Preview transition baseline');
    expect(text).not.toContain('Preview repair');
  });

  /**
   * The page is a link hub only, so it needs no data-access or notification dependency: rendering
   * it with no provider but the router proves it cannot reach a costing or import service.
   */
  it('renders without any maintenance service dependency', async () => {
    await expect(render()).resolves.toBeDefined();
  });
});
