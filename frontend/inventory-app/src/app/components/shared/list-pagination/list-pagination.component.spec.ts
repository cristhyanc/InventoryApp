import { TestBed } from '@angular/core/testing';
import { ListPaginationComponent } from './list-pagination.component';

describe('ListPaginationComponent (issue #1093)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(totalItems: number, pageIndex: number, pageSize: number) {
    await TestBed.configureTestingModule({ imports: [ListPaginationComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ListPaginationComponent);
    fixture.componentRef.setInput('totalItems', totalItems);
    fixture.componentRef.setInput('pageIndex', pageIndex);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('pageSizeOptions', [5, 10, 20, 50, 100]);
    fixture.componentRef.setInput('itemLabel', 'products');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const pageIndexChange = jest.fn();
    const pageSizeChange = jest.fn();
    fixture.componentInstance.pageIndexChange.subscribe(pageIndexChange);
    fixture.componentInstance.pageSizeChange.subscribe(pageSizeChange);
    const q = <T extends HTMLElement>(id: string) => host.querySelector(`[data-testid="list-pagination-${id}"]`) as T;
    return { host, q, pageIndexChange, pageSizeChange };
  }

  const clean = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

  it('states the range and the page, with Previous disabled on the first page', async () => {
    const { q } = await render(71, 0, 10);

    expect(clean(q('range').textContent)).toBe('Showing 1–10 of 71 products');
    expect(clean(q('page').textContent)).toBe('Page 1 of 8');
    expect(q<HTMLButtonElement>('previous').disabled).toBe(true);
    expect(q<HTMLButtonElement>('next').disabled).toBe(false);
  });

  it('disables Next on the last page and shows its short range', async () => {
    const { q } = await render(71, 7, 10);

    expect(clean(q('range').textContent)).toBe('Showing 71–71 of 71 products');
    expect(q<HTMLButtonElement>('next').disabled).toBe(true);
  });

  it('offers exactly the configured sizes and reports a chosen one', async () => {
    const { q, pageSizeChange } = await render(71, 0, 10);
    const select = q<HTMLSelectElement>('size');

    expect(Array.from(select.options).map((option) => option.value)).toEqual(['5', '10', '20', '50', '100']);
    expect(select.value).toBe('10');

    select.value = '20';
    select.dispatchEvent(new Event('change'));
    expect(pageSizeChange).toHaveBeenCalledWith(20);
  });

  it('reports Previous and Next as page indexes', async () => {
    const { q, pageIndexChange } = await render(71, 3, 10);

    q<HTMLButtonElement>('next').click();
    q<HTMLButtonElement>('previous').click();

    expect(pageIndexChange.mock.calls).toEqual([[4], [2]]);
  });
});
