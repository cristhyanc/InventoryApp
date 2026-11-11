import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PurchaseReceiptPreviewComponent } from './purchase-receipt-preview.component';

/** `PurchaseReceiptPreviewComponent` (issue #1166): the scanned receipt, beside the lines it made. */
describe('PurchaseReceiptPreviewComponent', () => {
  let fixture: ComponentFixture<PurchaseReceiptPreviewComponent>;
  let host: HTMLElement;
  let created: Blob[];
  let revoked: string[];

  beforeEach(async () => {
    created = [];
    revoked = [];
    Object.defineProperty(URL, 'createObjectURL', {
      value: (blob: Blob) => {
        created.push(blob);
        return `blob:receipt-${created.length}`;
      },
      configurable: true
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      value: (url: string) => revoked.push(url),
      configurable: true
    });

    await TestBed.configureTestingModule({ imports: [PurchaseReceiptPreviewComponent] }).compileComponents();
    fixture = TestBed.createComponent(PurchaseReceiptPreviewComponent);
    host = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => TestBed.resetTestingModule());

  function show(file: File | null): void {
    fixture.componentRef.setInput('file', file);
    fixture.detectChanges();
  }

  const byTestId = (id: string) => host.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('renders nothing at all while there is no receipt to show', () => {
    show(null);

    expect(byTestId('purchase-scanned-receipt')).toBeNull();
    expect(created).toHaveLength(0);
  });

  it('shows a photo as a picture, named and with alternative text', () => {
    const file = new File(['bytes'], 'receipt.jpg', { type: 'image/jpeg' });
    show(file);

    const image = byTestId('purchase-scanned-receipt-image') as HTMLImageElement;
    expect(image.getAttribute('src')).toBe('blob:receipt-1');
    expect(image.getAttribute('alt')).toBe('The scanned receipt receipt.jpg');
    expect(byTestId('purchase-scanned-receipt')?.textContent).toContain('receipt.jpg');
    expect(created).toEqual([file]);
  });

  it('opens the receipt full size in a new tab, because the pane beside the lines is small', () => {
    const open = jest.fn();
    Object.defineProperty(window, 'open', { value: open, configurable: true });
    show(new File(['bytes'], 'receipt.jpg', { type: 'image/jpeg' }));

    (byTestId('purchase-scanned-receipt-open-full') as HTMLButtonElement).click();

    expect(open).toHaveBeenCalledWith('blob:receipt-1', '_blank', 'noopener');
  });

  it('hands a PDF to the browser, with an Open in a new tab fallback for one that cannot show it', () => {
    show(new File(['bytes'], 'receipt.pdf', { type: 'application/pdf' }));

    const object = byTestId('purchase-scanned-receipt-document') as HTMLObjectElement;
    expect(object.getAttribute('type')).toBe('application/pdf');
    expect(object.getAttribute('data')).toBe('blob:receipt-1');
    expect(byTestId('purchase-scanned-receipt-image')).toBeNull();
    expect(byTestId('purchase-scanned-receipt-open')).not.toBeNull();
  });

  it('releases the previous receipt before showing another, and on destroy', () => {
    show(new File(['bytes'], 'first.jpg', { type: 'image/jpeg' }));
    show(new File(['bytes'], 'second.jpg', { type: 'image/jpeg' }));

    expect(revoked).toEqual(['blob:receipt-1']);
    expect(byTestId('purchase-scanned-receipt-image')?.getAttribute('src')).toBe('blob:receipt-2');

    fixture.destroy();

    expect(revoked).toEqual(['blob:receipt-1', 'blob:receipt-2']);
  });

  it('creates one object URL per receipt, however often it is re-rendered', () => {
    const file = new File(['bytes'], 'receipt.jpg', { type: 'image/jpeg' });
    show(file);
    show(file);
    fixture.detectChanges();

    expect(created).toHaveLength(1);
  });
});
